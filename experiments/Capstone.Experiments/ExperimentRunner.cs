using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capstone.Api;
using Capstone.Core;
using Capstone.Postgres;
using Npgsql;

namespace Capstone.Experiments;

internal sealed class ExperimentRunner(NpgsqlDataSource source)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<RunEvidence> RunCellAsync(RunCell cell, WorkloadSettings settings, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        var database = new LabDatabase(source);
        var initial = LabDatabase.MakeSeed(cell.Seed, settings.Accounts);
        var warmup = Workload.Generate(settings, cell, settings.WarmupSeconds, cell.Seed ^ 0x41d5405f);
        var measurement = Workload.Generate(settings, cell, settings.MeasureSeconds, cell.Seed);
        var admission = new LabAdmission();
        var serverTrace = new ConcurrentQueue<ServerAttempt>();
        var droppedAttempts = new ConcurrentDictionary<Guid, byte>();
        long start = Stopwatch.GetTimestamp();
        await using var app = LabApi.Build(source, cell.Group, port: 0, enableFaults: true,
            settings: new TransferPolicy(RetryLimit: settings.MaxTransactionRetries, LockTimeoutMs: settings.LockTimeoutMs,
                InitialBackoffMs: settings.InitialBackoffMs, BackoffCapMs: settings.BackoffCapMs,
                JitterSeed: cell.Seed),
            observe: x =>
            {
                serverTrace.Enqueue(new(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                    x with { ReceivedTick = 0 }, Stopwatch.GetElapsedTime(start, x.ReceivedTick).TotalMilliseconds));
                if (x.ResponseDropped)
                    droppedAttempts.TryAdd(x.Request.AttemptId, 0);
            },
            admission: admission);
        await app.StartAsync(ct);
        using var client = new HttpClient(new SocketsHttpHandler
            { UseProxy = false, MaxConnectionsPerServer = settings.MaxInFlight * 2 })
        {
            BaseAddress = new Uri(app.Urls.Single()),
            Timeout = Timeout.InfiniteTimeSpan
        };

        var auditor = new LedgerAuditor(source);
        var setupDone = elapsed.Elapsed;
        string initialHash = EvidenceFiles.HashJson(initial);
        await ResetAndVerifyAsync();
        var firstResetDone = elapsed.Elapsed;
        await RunWindowAsync(warmup, settings.WarmupSeconds, drainSeconds: 0, sampleResources: false, ct);
        var warmupDone = elapsed.Elapsed;
        await ResetAndVerifyAsync();
        var secondResetDone = elapsed.Elapsed;
        var observed = await RunWindowAsync(measurement, settings.MeasureSeconds, settings.DrainSeconds, sampleResources: true, ct);
        var measurementDone = elapsed.Elapsed;
        var ledger = await auditor.ReadSnapshotAsync(ct);
        var finishedAt = elapsed.Elapsed;
        return new(Guid.NewGuid().ToString("N"), started, cell, settings, initial, observed, ledger, finishedAt.TotalSeconds,
            new(setupDone.TotalSeconds, (firstResetDone - setupDone + secondResetDone - warmupDone).TotalSeconds,
                (warmupDone - firstResetDone).TotalSeconds, (measurementDone - secondResetDone).TotalSeconds,
                (finishedAt - measurementDone).TotalSeconds), initialHash);

        async Task ResetAndVerifyAsync()
        {
            try
            {
                await database.ResetAsync(initial, ct);
                var seeded = await auditor.ReadSnapshotAsync(ct);
                var actual = seeded.Accounts.Select(x => new SeedAccount(x.Id, x.Balance, x.Active)).ToArray();
                await using var keys = source.CreateCommand("SELECT count(*) FROM capstone.idempotency_records");
                if (!initial.SequenceEqual(actual) || seeded.Accounts.Any(x => x.Version != 0) ||
                    seeded.Transfers.Count != 0 || seeded.Entries.Count != 0 ||
                    (long)(await keys.ExecuteScalarAsync(ct))! != 0)
                    throw new StudyInfrastructureException("reset-contract");
            }
            catch (NpgsqlException)
            {
                throw new StudyInfrastructureException("reset-database");
            }
        }

        async Task<WindowEvidence> RunWindowAsync(LogicalInput[] inputs, int seconds, int drainSeconds, bool sampleResources,
            CancellationToken cancellationToken)
        {
            serverTrace.Clear();
            droppedAttempts.Clear();
            start = Stopwatch.GetTimestamp();
            double windowMs = seconds * 1000;
            double cutoffMs = windowMs + drainSeconds * 1000;
            string phase = admission.Open();
            using var cutoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cutoff.CancelAfter(TimeSpan.FromMilliseconds(cutoffMs));
            using var samplerStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var resources = sampleResources ? SampleAsync(start, samplerStop.Token) : Task.FromResult<RuntimeSample[]>([]);
            var outcomes = new Task<LogicalRun>[inputs.Length];
            using var workers = new SemaphoreSlim(settings.MaxInFlight);
            try
            {
                foreach (var input in inputs)
                {
                    double due = input.Order * 1000d / settings.OfferedPerSecond;
                    await DelayUntilAsync(start, due, cancellationToken);
                    if (TimeMs() >= windowMs)
                    {
                        outcomes[input.Order] = Task.FromResult(new LogicalRun(input, due, [], "window"));
                        continue;
                    }
                    if (!workers.Wait(0))
                    {
                        outcomes[input.Order] = Task.FromResult(new LogicalRun(input, due, [], "capacity"));
                        continue;
                    }
                    outcomes[input.Order] = DispatchAsync(input, due);
                }
                var pending = Task.WhenAll(outcomes);
                await DelayUntilAsync(start, cutoffMs, cancellationToken);
                bool activeAtCutoff = !pending.IsCompleted;
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var closed = admission.CloseAsync(settle.Token);
                await cutoff.CancelAsync();
                await samplerStop.CancelAsync();
                await closed;
                var finished = await pending.WaitAsync(settle.Token);
                var sampled = await resources.WaitAsync(settle.Token);
                await ConfirmNoTransactionAsync(settle.Token);
                return new(cutoffMs, activeAtCutoff, finished, serverTrace.ToArray(), sampled);
            }
            catch (Exception failure)
            {
                using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var closed = admission.CloseAsync(settle.Token);
                await cutoff.CancelAsync();
                await samplerStop.CancelAsync();
                try
                {
                    await closed;
                    await Task.WhenAll(outcomes.OfType<Task<LogicalRun>>()).WaitAsync(settle.Token);
                    await resources.WaitAsync(settle.Token);
                }
                catch (Exception) when (settle.IsCancellationRequested)
                {
                    // Do not read the database or reset it if the handlers cannot be drained.
                }
                catch (Exception)
                {
                    // Preserve the first failure and any completed records.
                }
                string reason = failure switch
                {
                    OperationCanceledException when cancellationToken.IsCancellationRequested => "interrupted",
                    OperationCanceledException or TimeoutException => "quiescence-timeout",
                    NpgsqlException => "database-or-sampler",
                    StudyInfrastructureException infrastructure => infrastructure.Code,
                    _ => "capture-defect"
                };
                throw new StudyInfrastructureException(reason, new PartialWindow(cell, sampleResources ? "measurement" : "warmup",
                    inputs.Length, outcomes.Where(t => t?.IsCompletedSuccessfully == true).Select(t => t.Result).ToArray(),
                    serverTrace.ToArray(), resources.IsCompletedSuccessfully ? resources.Result : []));
            }

            async Task<LogicalRun> DispatchAsync(LogicalInput input, double scheduled)
            {
                try
                {
                    double dispatch = TimeMs();
                    if (dispatch >= windowMs)
                        return new(input, scheduled, [], "window");
                    bool fault = input.RepeatAssigned && cell.Scenario == "W4";
                    var first = SendAsync(input.Request, repeat: false, fault, dispatch);
                    var repeat = input.Request with { AttemptId = input.RepeatAttemptId };
                    if (input.RepeatAssigned && cell.Scenario == "W2")
                    {
                        var second = SendAsync(repeat, repeat: true, fault: false);
                        return new(input, scheduled, await Task.WhenAll(first, second));
                    }

                    var result = await first;
                    if (fault && result.TransportFailure == "http-transport" && droppedAttempts.ContainsKey(input.Request.AttemptId) &&
                        TimeMs() < cutoffMs && !cutoff.IsCancellationRequested)
                    {
                        var retried = await SendAsync(repeat, repeat: true, fault: false);
                        return new(input, scheduled, [result, retried]);
                    }
                    return new(input, scheduled, [result]);
                }
                finally
                {
                    workers.Release();
                }
            }

            async Task<HttpAttempt> SendAsync(TransferRequest request, bool repeat, bool fault, double? dispatch = null)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cutoff.Token);
                deadline.CancelAfter(settings.AttemptTimeoutMs);
                using var message = new HttpRequestMessage(HttpMethod.Post, "/transfers") { Content = JsonContent.Create(request) };
                message.Headers.Add(LabAdmission.Header, phase);
                message.Headers.Add(LabApi.KeyHeader, request.LogicalRequestId.ToString("N"));
                if (fault)
                    message.Headers.Add(LabApi.FaultHeader, LabApi.ResponseLoss);
                double begin = dispatch ?? TimeMs();
                int? httpStatus = null;
                try
                {
                    using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    httpStatus = (int)response.StatusCode;
                    var reply = await response.Content.ReadFromJsonAsync<TransferReply>(Json, deadline.Token);
                    return new(request, repeat, begin, TimeMs(), httpStatus, reply, reply is null ? "empty-reply" : null);
                }
                catch (HttpRequestException)
                {
                    return new(request, repeat, begin, TimeMs(), httpStatus, null, "http-transport");
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    return new(request, repeat, begin, TimeMs(), httpStatus, null,
                        cutoff.IsCancellationRequested ? "window-cutoff" : "attempt-timeout");
                }
                catch (JsonException)
                {
                    return new(request, repeat, begin, TimeMs(), httpStatus, null, "invalid-reply");
                }
            }

            double TimeMs() => Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    private async Task ConfirmNoTransactionAsync(CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = current_database() AND application_name = 'mse907-capstone'
                AND pid <> pg_backend_pid() AND xact_start IS NOT NULL
            """);
        if ((long)(await command.ExecuteScalarAsync(ct))! != 0)
            throw new StudyInfrastructureException("database-not-quiescent");
    }

    private async Task<RuntimeSample[]> SampleAsync(long start, CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        var snapshots = new List<RuntimeSample>();
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand("""
                SELECT count(*)::int, count(*) FILTER (WHERE state = 'active')::int,
                    count(*) FILTER (WHERE wait_event_type = 'Lock')::int
                FROM pg_stat_activity
                WHERE datname = current_database() AND application_name = 'mse907-capstone' AND pid <> pg_backend_pid()
                """, connection);
            while (true)
            {
                await using (var rows = await command.ExecuteReaderAsync(ct))
                {
                    if (await rows.ReadAsync(ct))
                    {
                        process.Refresh();
                        snapshots.Add(new(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                            process.TotalProcessorTime.TotalSeconds, process.WorkingSet64,
                            rows.GetInt32(0), rows.GetInt32(1), rows.GetInt32(2)));
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return snapshots.ToArray();
        }
    }

    private static async Task DelayUntilAsync(long start, double due, CancellationToken ct)
    {
        while (due - Stopwatch.GetElapsedTime(start).TotalMilliseconds is > 0 and var remaining)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Floor(remaining))), ct);
    }
}

internal sealed record PartialWindow(RunCell Cell, string Phase, int Offered, LogicalRun[] Completed,
    ServerAttempt[] Server, RuntimeSample[] Resources);

internal sealed class StudyInfrastructureException(string code, PartialWindow? partial = null) : Exception(code)
{
    public string Code { get; } = code;
    public PartialWindow? Partial { get; } = partial;
}
