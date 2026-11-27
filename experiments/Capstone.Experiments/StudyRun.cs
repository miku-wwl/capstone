using System.Diagnostics;
using System.Net.Sockets;
using Capstone.Postgres;
using Npgsql;

namespace Capstone.Experiments;

internal sealed record StudyFailure(string Reason, RunCell Cell, DateTimeOffset RecordedUtc, PartialWindow? Partial,
    string? ErrorType = null, int? ErrorCode = null);

internal static class StudyRun
{
    public static async Task ExecuteAsync(RunPlan plan, string root, string scopeSha, BudgetLimit budget,
        int throughBlock, FormalFreeze? freeze, string? freezeSha, CancellationToken ct)
    {
        if (throughBlock < 1 || throughBlock > plan.Blocks)
            throw new ArgumentOutOfRangeException(nameof(throughBlock));
        var startup = Stopwatch.StartNew();
        Directory.CreateDirectory(root);
        string checkpointPath = Path.Combine(root, StudyCheckpoint.Filename);
        using var ownership = new FileStream(Path.Combine(root, ".study-lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        if (!File.Exists(checkpointPath) && Directory.EnumerateFileSystemEntries(root)
                .Any(p => Path.GetFileName(p) != ".study-lock"))
            throw new InvalidDataException("Use a dedicated empty dataset directory or an existing study checkpoint.");
        await using var source = LabDatabase.FromEnvironment();
        await using var exclusive = await source.OpenConnectionAsync(ct);
        await using var advisory = new NpgsqlCommand("SELECT pg_try_advisory_lock(907002)", exclusive);
        if (!(bool)(await advisory.ExecuteScalarAsync(ct))!)
            throw new InvalidOperationException("Another study process owns the synthetic lab.");
        await new LabDatabase(source).MigrateAsync(ct);
        int poolLimit = new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize;
        var identity = await StudyIdentity.CaptureAsync(exclusive, poolLimit, ct);
        if (freeze is not null && !freeze.Identity.SameExecutionAs(identity))
            throw new InvalidDataException("The dataset runtime, code or build differs from the frozen execution identity.");
        var binding = new StudyBinding(plan, EvidenceFiles.HashJson(plan.Settings), scopeSha, freezeSha, identity, budget);
        var state = File.Exists(checkpointPath)
            ? await ReadAndVerifyAsync(root, complete: false, ct)
            : new StudyCheckpoint(binding, DateTimeOffset.UtcNow);
        if (!state.Binding.Identity.SameExecutionAs(identity) || state.Binding.FreezeSha256 != freezeSha ||
            state.Binding.Budget != budget || state.Binding.ScopeSha256 != scopeSha ||
            EvidenceFiles.HashJson(state.Binding.Plan) != EvidenceFiles.HashJson(plan))
            throw new InvalidDataException("Resume requires the original code, binary, environment, scope and plan fingerprints.");
        bool recovered = state.Attempts.LastOrDefault()?.State == BlockState.Running;
        await state.RecoverInterruptedAsync(root, ct);
        if (recovered) await SaveAsync();
        if (state.CompletedBlocks >= throughBlock && state.Attempts.All(b => b.State != BlockState.Running))
            return;
        state.SetupSeconds += startup.Elapsed.TotalSeconds;
        await SaveAsync();

        var runner = new ExperimentRunner(source);
        while (state.CompletedBlocks < throughBlock)
        {
            state.EnsureBudgetForCell();
            int block = state.CompletedBlocks + 1;
            var attempt = state.Start(block);
            Directory.CreateDirectory(Path.Combine(root, attempt.Folder));
            await SaveAsync();
            var cells = plan.Cells.Where(x => x.Block == block).ToArray();
            for (int slot = 0; slot < cells.Length; slot++)
            {
                ct.ThrowIfCancellationRequested();
                try { state.EnsureBudgetForCell(); }
                catch (InvalidDataException)
                {
                    SetOutcome(BlockState.InstrumentationStop, "machine-time-budget");
                    await SaveAsync();
                    throw;
                }
                var cell = cells[slot];
                var measured = Stopwatch.StartNew();
                attempt.ActiveSlot = slot;
                await SaveAsync();
                using var remaining = CancellationTokenSource.CreateLinkedTokenSource(ct);
                remaining.CancelAfter(TimeSpan.FromSeconds(state.Binding.Budget.LimitSeconds - state.BudgetChargeSeconds));
                double recordedSeconds = 0;
                try
                {
                    await identity.VerifyFilesAsync(remaining.Token);
                    string serverEpoch = await StudyIdentity.DatabaseEpochAsync(exclusive, remaining.Token);
                    var raw = await runner.RunCellAsync(cell, plan.Settings, remaining.Token);
                    string relative = StudyCheckpoint.FileFor(attempt, slot, cell);
                    string path = StudyCheckpoint.UnderBlock(root, attempt, relative);
                    var writing = Stopwatch.StartNew();
                    await EvidenceFiles.WriteAsync(path, raw, replace: false, CancellationToken.None);
                    string hash = await EvidenceFiles.HashAsync(path, CancellationToken.None);
                    double writeSeconds = writing.Elapsed.TotalSeconds;
                    var analysis = Stopwatch.StartNew();
                    var summary = ProtocolAudit.Summarize(raw);
                    bool databaseRestarted = await StudyIdentity.DatabaseEpochAsync(exclusive, remaining.Token) != serverEpoch;
                    recordedSeconds = measured.Elapsed.TotalSeconds;
                    attempt.Runs.Add(new(relative, hash, summary, recordedSeconds,
                        analysis.Elapsed.TotalSeconds, writeSeconds));
                    attempt.ActiveSlot = null;
                    if (databaseRestarted)
                        SetOutcome(BlockState.Invalid, "database-restarted-inside-cell");
                    else if (summary.InstrumentationIssues.Length != 0)
                        SetOutcome(BlockState.InstrumentationStop, "instrumentation-integrity");
                    else if (summary.FinancialIssues.Length != 0 || summary.PersistentKeyViolation)
                        SetOutcome(BlockState.ObservedSafetyStop, "observed-financial-or-persistent-key-violation");
                    else if (plan.Phase == "engineering" && (summary.Outcomes.CorrectlyConfirmed == 0 ||
                             summary.ClientActiveAtCutoff))
                        SetOutcome(BlockState.InstrumentationStop, "engineering-smoke-contract");
                    else if (slot == cells.Length - 1)
                        attempt.State = BlockState.Complete;
                    await SaveAsync();
                    Console.WriteLine($"{plan.Phase} block={block:D2} attempt={attempt.Attempt} cell={slot + 1:D2}/16 " +
                        $"{cell.Scenario} {cell.Group} offered={summary.Offered} dispatched={summary.Dispatched} " +
                        $"confirmed={summary.Outcomes.CorrectlyConfirmed} duplicate={summary.DuplicateLogical} " +
                        $"injected={summary.Exposure.InjectedTransportLosses} state={attempt.State} " +
                        $"machineHours={state.MeasuredMachineSeconds / 3600:F3}");
                    if (attempt.State is not (BlockState.Running or BlockState.Complete))
                        throw new InvalidDataException($"Study stopped with preserved raw data: {attempt.Reason}.");
                }
                catch (Exception error) when (attempt.State == BlockState.Running)
                {
                    string reason = error switch
                    {
                        StudyInfrastructureException infrastructure => infrastructure.Code,
                        OperationCanceledException => "interrupted",
                        NpgsqlException => "database-connection",
                        IOException or UnauthorizedAccessException => "evidence-or-environment-io",
                        SocketException => "local-listener",
                        _ => "capture-defect"
                    };
                    bool budgetExpired = remaining.IsCancellationRequested && !ct.IsCancellationRequested;
                    if (budgetExpired) reason = "machine-time-budget";
                    SetOutcome(reason is "capture-defect" or "machine-time-budget"
                        ? BlockState.InstrumentationStop : BlockState.Invalid, reason);
                    attempt.FailedMachineSeconds += Math.Max(0, measured.Elapsed.TotalSeconds - recordedSeconds);
                    attempt.ActiveSlot = null;
                    var failure = new StudyFailure(reason, cell, DateTimeOffset.UtcNow,
                        (error as StudyInfrastructureException)?.Partial, error.GetType().Name, error.HResult);
                    await SaveAsync();
                    await PreserveUnindexedAsync(root, attempt, failure, CancellationToken.None);
                    await SaveAsync();
                    throw new InvalidDataException($"Study stopped in block {block}, cell {slot + 1}: {reason}.");
                }
            }

            void SetOutcome(BlockState value, string reason)
            {
                attempt.State = value;
                attempt.Reason = reason;
            }
        }
        state.ValidateStructure(complete: state.Complete);
        Console.WriteLine($"Verified execution checkpoint: {state.CompletedBlocks}/{plan.Blocks} blocks; {state.MeasuredMachineSeconds:F1}s measured.");

        Task SaveAsync() => EvidenceFiles.WriteAsync(checkpointPath, state, replace: true, CancellationToken.None);
    }

    public static async Task<StudyCheckpoint> ReadAndVerifyAsync(string root, bool complete, CancellationToken ct)
    {
        var state = await EvidenceFiles.ReadAsync<StudyCheckpoint>(Path.Combine(root, StudyCheckpoint.Filename), ct);
        state.ValidateStructure(complete);
        foreach (var attempt in state.Attempts)
        {
            foreach (var indexed in attempt.Runs)
            {
                string path = StudyCheckpoint.UnderBlock(root, attempt, indexed.File);
                if (await EvidenceFiles.HashAsync(path, ct) != indexed.Sha256)
                    throw new InvalidDataException($"Raw run hash mismatch: {indexed.File}.");
                var raw = await EvidenceFiles.ReadAsync<RunEvidence>(path, ct);
                if (raw.Settings != state.Binding.Plan.Settings || raw.Cell != indexed.Summary.Cell ||
                    EvidenceFiles.HashJson(ProtocolAudit.Summarize(raw)) != EvidenceFiles.HashJson(indexed.Summary))
                    throw new InvalidDataException($"Independent data recomputation failed: {indexed.File}.");
            }
            foreach (var failed in attempt.FailureEvidence)
            {
                if (await EvidenceFiles.HashAsync(StudyCheckpoint.UnderBlock(root, attempt, failed.File), ct) != failed.Sha256)
                    throw new InvalidDataException($"Retained failure artifact hash mismatch: {failed.File}.");
            }
        }
        return state;
    }

    private static async Task PreserveUnindexedAsync(string root, BlockAttempt attempt, StudyFailure failure, CancellationToken ct)
    {
        string relative = $"{attempt.Folder}/failure-{Guid.NewGuid():N}.json.gz";
        await EvidenceFiles.WriteAsync(StudyCheckpoint.UnderBlock(root, attempt, relative), failure, replace: false, ct);
        var indexed = attempt.Runs.Select(r => r.File).ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, attempt.Folder)))
        {
            var file = $"{attempt.Folder}/{Path.GetFileName(path)}";
            if (!indexed.Contains(file))
                attempt.FailureEvidence.Add(new(file, await EvidenceFiles.HashAsync(path, ct)));
        }
    }
}
