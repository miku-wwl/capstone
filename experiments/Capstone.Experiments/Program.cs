using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Capstone.Experiments;
using Capstone.Postgres;
using Npgsql;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
try
{
    switch (args)
    {
        case ["plan", string phase, string blocks, string configuration, string workloadSeed, string orderSeed]:
            var plan = RunPlan.Create(phase, ParseInt(blocks), ParseInt(workloadSeed), ParseInt(orderSeed),
                await ReadAsync<WorkloadSettings>(configuration));
            Console.WriteLine(JsonSerializer.Serialize(plan, ExperimentRunner.Json));
            break;
        case ["smoke", string output]:
            var smoke = RunPlan.Create("engineering", 1, 907, 2026, new(Accounts: 100,
                OfferedPerSecond: 32, MaxInFlight: 16, WarmupSeconds: 1, MeasureSeconds: 1, DrainSeconds: 2));
            await RunAsync(smoke, output);
            break;
        case ["pilot", string configuration, string workloadSeed, string orderSeed, string decisionRecord, string output]:
            var pilot = RunPlan.Create("pilot", 5, ParseInt(workloadSeed), ParseInt(orderSeed),
                await ReadAsync<WorkloadSettings>(configuration));
            if (new FileInfo(decisionRecord).Length == 0)
                throw new InvalidDataException("Provide the documented institutional decision for this study.");
            await RunAsync(pilot, output, await HashAsync(decisionRecord));
            break;
        case ["verify", string directory]:
            var verified = await VerifyAsync(directory);
            Console.WriteLine($"Verified {verified.Runs.Length} raw hashes and independently recomputed cell summaries ({verified.Plan.Phase}).");
            break;
        case ["pilot-precision", string directory]:
            var precision = PilotPrecision.Evaluate(await VerifyAsync(directory));
            Console.WriteLine(JsonSerializer.Serialize(precision, ExperimentRunner.Json));
            break;
        default:
            Console.Error.WriteLine("Usage: plan <engineering|pilot|formal> <blocks> <settings.json> <workload-seed> <order-seed>");
            Console.Error.WriteLine("       smoke <new-output-directory> | verify <evidence-directory>");
            Console.Error.WriteLine("       pilot <settings.json> <workload-seed> <order-seed> <institutional-decision-file> <new-output-directory>");
            Console.Error.WriteLine("       pilot-precision <verified-pilot-directory>");
            return 2;
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is NpgsqlException npgsql
        ? npgsql is PostgresException pg ? $"PostgreSQL failed (SQLSTATE {pg.SqlState})." : "The PostgreSQL connection failed."
        : error.Message);
    return 1;
}

async Task<ExperimentManifest> VerifyAsync(string directory)
{
    var manifest = await ReadAsync<ExperimentManifest>(Path.Combine(directory, "manifest.json"));
    var expected = RunPlan.Create(manifest.Plan.Phase, manifest.Plan.Blocks, manifest.Plan.WorkloadSeed,
        manifest.Plan.OrderSeed, manifest.Plan.Settings).Cells;
    if (!expected.SequenceEqual(manifest.Plan.Cells) || expected.Length != manifest.Runs.Length)
        throw new InvalidDataException("The manifest does not contain every planned cell in the registered order.");
    for (int i = 0; i < expected.Length; i++)
    {
        var entry = manifest.Runs[i];
        if (Path.GetFileName(entry.File) != entry.File)
            throw new InvalidDataException("Invalid raw evidence path.");
        string path = Path.Combine(directory, entry.File);
        var raw = await ReadAsync<RunEvidence>(path);
        var summary = RunAudit.Summarize(raw);
        if (await HashAsync(path) != entry.Sha256 || raw.Cell != expected[i] ||
            raw.Settings != manifest.Plan.Settings ||
            JsonSerializer.Serialize(summary, ExperimentRunner.Json) != JsonSerializer.Serialize(entry.Summary, ExperimentRunner.Json))
            throw new InvalidDataException($"Manifest or independently recomputed metrics differ for {entry.File}.");
    }
    return manifest;
}

async Task RunAsync(RunPlan plan, string directory, string? decisionSha256 = null)
{
    if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        throw new IOException("Use an empty evidence directory.");
    Directory.CreateDirectory(directory);
    using var ownership = new FileStream(Path.Combine(directory, ".active"), FileMode.CreateNew, FileAccess.Write);
    await using var source = LabDatabase.FromEnvironment();
    await using var exclusive = await source.OpenConnectionAsync(stop.Token);
    await using var lockRunner = new NpgsqlCommand("SELECT pg_try_advisory_lock(907002)", exclusive);
    if (!(bool)(await lockRunner.ExecuteScalarAsync(stop.Token))!)
        throw new InvalidOperationException("Another experiment runner owns the lab database.");
    await new LabDatabase(source).MigrateAsync(stop.Token);
    var assembly = typeof(ExperimentRunner).Assembly.Location;
    var version = new RunEnvironment(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, exclusive.PostgreSqlVersion.ToString(),
        await GitAsync("rev-parse", "HEAD"), (await GitAsync("status", "--porcelain", "--untracked-files=normal")) is { Length: > 0 },
        await HashAsync(assembly));
    var runner = new ExperimentRunner(source);
    var runs = new List<RunArtifact>();
    var started = DateTimeOffset.UtcNow;
    foreach (var cell in plan.Cells)
    {
        var raw = await runner.RunCellAsync(cell, plan.Settings, stop.Token);
        var summary = RunAudit.Summarize(raw);
        string file = $"block-{cell.Block:D2}-{cell.Scenario}-{cell.Group}.json";
        string path = Path.Combine(directory, file);
        await WriteAsync(path, raw);
        runs.Add(new(file, await HashAsync(path), summary));
        await WriteAsync(Path.Combine(directory, "checkpoint.json"), new ExperimentManifest(plan, version, started, runs.ToArray(), decisionSha256));
        Console.WriteLine($"{cell.Block:D2} {cell.Scenario,-7} {cell.Group}: dispatched={summary.Dispatched} " +
            $"confirmed={summary.CorrectlyConfirmed} duplicate={summary.DuplicateLogical} triggered={summary.Triggered} " +
            $"unknown={summary.Unknown} audit={summary.AuditViolations.Length}");
        if (summary.AuditViolations.Length > 0 ||
            (plan.Phase == "engineering" && (summary.ClientActiveAtCutoff || summary.CorrectlyConfirmed == 0)))
            throw new InvalidDataException($"Cell {file} failed an audit or engineering check. Retained raw evidence.");
    }
    await WriteAsync(Path.Combine(directory, "manifest.json"), new ExperimentManifest(plan, version, started, runs.ToArray(), decisionSha256));
}

async Task<T> ReadAsync<T>(string path)
{
    await using var input = File.OpenRead(path);
    return await JsonSerializer.DeserializeAsync<T>(input, ExperimentRunner.Json, stop.Token)
        ?? throw new InvalidDataException($"Missing JSON content in {Path.GetFileName(path)}.");
}

async Task WriteAsync<T>(string path, T value)
{
    string pending = path + ".partial";
    await using (var output = File.Create(pending))
        await JsonSerializer.SerializeAsync(output, value, ExperimentRunner.Json, stop.Token);
    File.Move(pending, path, overwrite: true);
}

async Task<string> HashAsync(string path)
{
    await using var input = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(input, stop.Token)).ToLowerInvariant();
}

async Task<string?> GitAsync(params string[] command)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in command)
        start.ArgumentList.Add(argument);
    try
    {
        using var process = Process.Start(start)!;
        string value = await process.StandardOutput.ReadToEndAsync(stop.Token);
        await process.WaitForExitAsync(stop.Token);
        return process.ExitCode == 0 ? value.Trim() : null;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return null;
    }
}

static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);

internal sealed record RunEnvironment(string Runtime, string Os, string Architecture, int LogicalProcessors,
    string PostgreSql, string? GitHead, bool GitWorkTreeDirty, string RunnerSha256);
internal sealed record RunArtifact(string File, string Sha256, RunSummary Summary);
internal sealed record ExperimentManifest(RunPlan Plan, RunEnvironment Environment, DateTimeOffset StartedUtc,
    RunArtifact[] Runs, string? DecisionSha256 = null);
