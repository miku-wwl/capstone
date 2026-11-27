using Capstone.Postgres;
using Npgsql;

namespace Capstone.Experiments;

internal static class PilotRecovery
{
    private const string RecoveryReason = "interrupted-checkpoint-publication";
    private static readonly HashSet<string> PublicationFiles = new(StringComparer.Ordinal)
    {
        "experiments/Capstone.Experiments/EvidenceFiles.cs",
        "experiments/Capstone.Experiments/PilotRecovery.cs",
        "experiments/Capstone.Experiments/Program.cs",
        "experiments/Capstone.Experiments/StudyCheckpoint.cs",
        "experiments/Capstone.Experiments/StudyRun.cs"
    };

    public static async Task RecoverAsync(string beforeRoot, string afterRoot, double processSeconds, CancellationToken ct)
    {
        var prior = await StudyRun.ReadAndVerifyAsync(beforeRoot, complete: false, ct);
        var interrupted = prior.Attempts.LastOrDefault();
        if (prior.Binding.Plan.Phase != "pilot" || prior.Recovery is not null || prior.InvalidBlocks != 0 ||
            interrupted is null || interrupted.State != BlockState.InstrumentationStop || interrupted.Reason != "capture-defect" ||
            interrupted.ActiveSlot is not null || interrupted.Runs.Count is < 1 or > 15 || interrupted.FailureEvidence.Count != 1)
            throw new InvalidDataException("This recovery requires an unambiguous interrupted pilot checkpoint publication.");
        var pendingPath = Directory.EnumerateFiles(beforeRoot, StudyCheckpoint.Filename + ".*.pending").Single();
        var pending = await EvidenceFiles.ReadAsync<StudyCheckpoint>(pendingPath, ct);
        pending.ValidateStructure(complete: false);
        var unpublished = pending.Attempts.Last();
        if (pending.Attempts.Count != prior.Attempts.Count ||
            EvidenceFiles.HashJson(prior.Binding) != EvidenceFiles.HashJson(pending.Binding) ||
            !pending.Attempts.Take(pending.Attempts.Count - 1).Select(EvidenceFiles.HashJson)
                .SequenceEqual(prior.Attempts.Take(prior.Attempts.Count - 1).Select(EvidenceFiles.HashJson)) ||
            unpublished.State != BlockState.Running || unpublished.ActiveSlot is not null ||
            EvidenceFiles.HashJson(unpublished.Runs) != EvidenceFiles.HashJson(interrupted.Runs) ||
            !double.IsFinite(processSeconds) || processSeconds < pending.MeasuredMachineSeconds ||
            processSeconds > pending.MeasuredMachineSeconds + 60)
            throw new InvalidDataException("The intact pending checkpoint does not match the retained runs and process timing.");
        var record = await EvidenceFiles.ReadAsync<StudyFailure>(
            StudyCheckpoint.UnderBlock(beforeRoot, interrupted, interrupted.FailureEvidence[0].File), ct);
        if (record.Partial is not null || record.Cell != interrupted.Runs[^1].Summary.Cell ||
            interrupted.Runs.Any(r => r.Summary.FinancialIssues.Length != 0 ||
                r.Summary.PersistentKeyViolation || r.Summary.InstrumentationIssues.Length != 0))
            throw new InvalidDataException("A measured failure cannot be treated as an interrupted publication.");

        await using var source = LabDatabase.FromEnvironment();
        await using var connection = await source.OpenConnectionAsync(ct);
        var identity = await StudyIdentity.CaptureAsync(connection,
            new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize, ct);
        VerifyPublicationOnlyChange(prior.Binding.Identity, identity);

        string original = Path.Combine(beforeRoot, StudyCheckpoint.Filename);
        string originalHash = await EvidenceFiles.HashAsync(original, ct);
        string pendingHash = await EvidenceFiles.HashAsync(pendingPath, ct);
        Directory.CreateDirectory(afterRoot);
        if (Directory.EnumerateFileSystemEntries(afterRoot).Any())
            throw new InvalidDataException("Use a fresh recovery dataset directory.");
        foreach (var block in prior.Attempts)
        {
            Directory.CreateDirectory(Path.Combine(afterRoot, block.Folder));
            foreach (string relative in block.Runs.Select(r => r.File).Concat(block.FailureEvidence.Select(f => f.File)))
                File.Copy(StudyCheckpoint.UnderBlock(beforeRoot, block, relative), StudyCheckpoint.UnderBlock(afterRoot, block, relative));
        }
        interrupted.State = BlockState.Invalid;
        interrupted.Reason = RecoveryReason;
        // The interrupted checkpoint already indexed the measured cell. Its failure handler also charged that cell.
        interrupted.FailedMachineSeconds = 0;
        Preserve(original, "checkpoint-before-recovery.json", originalHash);
        Preserve(pendingPath, "checkpoint-unpublished.json", pendingHash);
        var resumed = new StudyCheckpoint(prior.Binding with { Identity = identity }, prior.StartedUtc)
        {
            Attempts = prior.Attempts,
            SetupSeconds = prior.SetupSeconds + (processSeconds - pending.MeasuredMachineSeconds),
            Recovery = new(originalHash, pendingHash, prior.Binding.Identity.SourceSha256, prior.Binding.Identity.BuildSha256,
                identity.BuildSha256, prior.CompletedBlocks, interrupted.Block, processSeconds, RecoveryReason, DateTimeOffset.UtcNow)
        };
        resumed.ValidateStructure(complete: false);
        await EvidenceFiles.WriteAsync(Path.Combine(afterRoot, StudyCheckpoint.Filename), resumed, replace: false, ct);
        Console.WriteLine($"Retained {resumed.CompletedBlocks} complete blocks. Block {interrupted.Block} will rerun in full.");

        void Preserve(string sourcePath, string name, string hash)
        {
            string relative = $"{interrupted.Folder}/{name}";
            File.Copy(sourcePath, StudyCheckpoint.UnderBlock(afterRoot, interrupted, relative));
            interrupted.FailureEvidence.Add(new(relative, hash));
        }
    }

    private static void VerifyPublicationOnlyChange(StudyIdentity prior, StudyIdentity current)
    {
        var measuredFiles = new Func<StudyIdentity, IEnumerable<FileFingerprint>>(id =>
            id.SourceFiles.Where(f => !PublicationFiles.Contains(f.File)));
        var libraries = new Func<StudyIdentity, IEnumerable<FileFingerprint>>(id =>
            id.BuildFiles.Where(f => f.File != "Capstone.Experiments.dll"));
        if (prior.Runtime != current.Runtime || !measuredFiles(prior).SequenceEqual(measuredFiles(current)) ||
            !libraries(prior).SequenceEqual(libraries(current)))
            throw new InvalidDataException("A measurement source, dependency or runtime changed. Start a separate pilot dataset.");
    }
}
