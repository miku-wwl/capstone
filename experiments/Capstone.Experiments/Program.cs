using System.Globalization;
using System.Text.Json;
using Capstone.Experiments;
using Npgsql;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
var ct = stop.Token;
try
{
    switch (args)
    {
        case ["plan", string phase, string blocks, string settings, string workSeed, string orderSeed]:
            Print(RunPlan.Create(phase, Int(blocks), Int(workSeed), Int(orderSeed),
                await EvidenceFiles.ReadAsync<WorkloadSettings>(settings, ct)));
            break;
        case ["smoke", string output]:
            var smoke = RunPlan.Create("engineering", 1, 907, 2026, new(Accounts: 100,
                OfferedPerSecond: 32, MaxInFlight: 16, WarmupSeconds: 1, MeasureSeconds: 1, DrainSeconds: 2));
            await StudyRun.ExecuteAsync(smoke, output, "engineering-only", new(600, 0, 1), 1, null, null, ct);
            break;
        case ["pilot", string settings, string workSeed, string orderSeed, string scope, string output]:
            var pilot = RunPlan.Create("pilot", 5, Int(workSeed), Int(orderSeed),
                await EvidenceFiles.ReadAsync<WorkloadSettings>(settings, ct));
            await StudyRun.ExecuteAsync(pilot, output, await StudyScope.ReadHashAsync(scope, ct),
                new(86400, 0, 1), 5, null, null, ct);
            break;
        case ["recover-pilot-checkpoint", string stopped, string resumed, string processSeconds]:
            await PilotRecovery.RecoverAsync(stopped, resumed, Seconds(processSeconds), ct);
            break;
        case ["freeze", string pilotRoot, string settingsOut, string workSeed, string orderSeed, string prepSeconds]:
            var draft = await FormalFreeze.CreateAsync(pilotRoot, Int(workSeed), Int(orderSeed), Seconds(prepSeconds), ct);
            await EvidenceFiles.WriteAsync(settingsOut, draft, replace: false, ct);
            Console.WriteLine(await EvidenceFiles.HashAsync(settingsOut, ct));
            break;
        case ["formal", string protocol, string expectedHash, string output, string throughBlock]:
            var actualHash = await EvidenceFiles.HashAsync(protocol, ct);
            if (actualHash != expectedHash.ToLowerInvariant())
                throw new InvalidDataException("The supplied formal protocol SHA-256 does not match its frozen bytes.");
            var frozen = await EvidenceFiles.ReadAsync<FormalFreeze>(protocol, ct);
            frozen.Validate();
            await StudyIdentity.VerifyFormalCommitAsync(frozen.CodeCommit, protocol, ct);
            await StudyRun.ExecuteAsync(frozen.Plan, output, frozen.ScopeSha256, frozen.Budget, Int(throughBlock),
                frozen, actualHash, ct);
            break;
        case ["verify" or "verify-progress", string folder]:
            if (File.Exists(Path.Combine(folder, StudyCheckpoint.Filename)))
            {
                var state = await StudyRun.ReadAndVerifyAsync(folder, args[0] == "verify", ct);
                Console.WriteLine($"Verified {state.Attempts.Sum(x => x.Runs.Count)} raw hashes and recomputed metrics " +
                    $"({state.Binding.Plan.Phase}, {state.CompletedBlocks}/{state.Binding.Plan.Blocks} complete blocks).");
            }
            else
            {
                await VerifyLegacyAsync(folder);
            }
            break;
        case ["pilot-precision", string folder, string setupSeconds]:
            Print(StudyPrecision.Calculate(await StudyRun.ReadAndVerifyAsync(folder, complete: true, ct), Seconds(setupSeconds)));
            break;
        default:
            Console.Error.WriteLine("""
                plan <engineering|pilot|formal> <blocks> <settings.json> <workload-seed> <order-seed>
                smoke <engineering-directory>
                pilot <settings.json> <workload-seed> <order-seed> <student-scope-record.json> <pilot-directory>
                recover-pilot-checkpoint <stopped-pilot> <new-pilot-directory> <original-measured-process-seconds>
                pilot-precision <pilot-directory> <measured-preparation-and-other-study-seconds>
                freeze <pilot-directory> <frozen-protocol.json> <formal-workload-seed> <formal-order-seed> <preparation-seconds>
                formal <frozen-protocol.json> <protocol-sha256> <formal-directory> <last-block-this-batch>
                verify <evidence-directory> | verify-progress <evidence-directory>
                """);
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

async Task VerifyLegacyAsync(string directory)
{
    var manifest = await EvidenceFiles.ReadAsync<ExperimentManifest>(Path.Combine(directory, "manifest.json"), ct);
    if (manifest.Plan.Phase != "engineering")
        throw new InvalidDataException("Only historic engineering evidence uses the legacy manifest format.");
    var expected = RunPlan.Create(manifest.Plan.Phase, manifest.Plan.Blocks, manifest.Plan.WorkloadSeed,
        manifest.Plan.OrderSeed, manifest.Plan.Settings).Cells;
    if (!expected.SequenceEqual(manifest.Plan.Cells) || expected.Length != manifest.Runs.Length)
        throw new InvalidDataException("The historical manifest does not contain every planned cell in order.");
    for (int i = 0; i < expected.Length; i++)
    {
        var entry = manifest.Runs[i];
        if (Path.GetFileName(entry.File) != entry.File)
            throw new InvalidDataException("Invalid historical evidence path.");
        string path = Path.Combine(directory, entry.File);
        var raw = await EvidenceFiles.ReadAsync<RunEvidence>(path, ct);
        if (await EvidenceFiles.HashAsync(path, ct) != entry.Sha256 || raw.Cell != expected[i] ||
            raw.Settings != manifest.Plan.Settings ||
            EvidenceFiles.HashJson(RunAudit.Summarize(raw)) != EvidenceFiles.HashJson(entry.Summary))
            throw new InvalidDataException($"Historical hash or recomputed metrics differ for {entry.File}.");
    }
    Console.WriteLine($"Verified {expected.Length} historical engineering raw hashes and cell summaries.");
}

static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);
static double Seconds(string value) => double.Parse(value, CultureInfo.InvariantCulture);
static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, ExperimentRunner.Json));

internal sealed record RunArtifact(string File, string Sha256, RunSummary Summary);
internal sealed record ExperimentManifest(RunPlan Plan, RunArtifact[] Runs);
