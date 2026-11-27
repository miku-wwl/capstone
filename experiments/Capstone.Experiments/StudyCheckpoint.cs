namespace Capstone.Experiments;

internal enum BlockState { Running, Complete, Invalid, ObservedSafetyStop, InstrumentationStop }
internal sealed record StudyArtifact(string File, string Sha256, ProtocolSummary Summary, double MachineSeconds,
    double AnalysisSeconds, double RawWriteSeconds);
internal sealed record FailedArtifact(string File, string Sha256);

internal sealed class BlockAttempt(int block, int attempt)
{
    public int Block { get; } = block;
    public int Attempt { get; } = attempt;
    public BlockState State { get; set; } = BlockState.Running;
    public string? Reason { get; set; }
    public int? ActiveSlot { get; set; }
    public double FailedMachineSeconds { get; set; }
    public double UnmeasuredBudgetChargeSeconds { get; set; }
    public List<StudyArtifact> Runs { get; init; } = [];
    public List<FailedArtifact> FailureEvidence { get; init; } = [];
    public string Folder => $"block-{Block:D2}/attempt-{Attempt:D2}";
}

internal sealed record BudgetLimit(double LimitSeconds, double PriorStudySeconds, int MaxInvalidBlocks);
internal sealed record StudyBinding(RunPlan Plan, string SettingsSha256, string ScopeSha256, string? FreezeSha256,
    StudyIdentity Identity, BudgetLimit Budget);

internal sealed record CheckpointRecovery(string OriginalCheckpointSha256, string OriginalPendingSha256,
    string PriorSourceSha256, string PriorBuildSha256, string ResumedBuildSha256,
    int RetainedCompleteBlocks, int InvalidatedBlock, double PriorProcessMachineSeconds,
    string Reason, DateTimeOffset RecoveredUtc);

internal sealed class StudyCheckpoint(StudyBinding binding, DateTimeOffset startedUtc)
{
    public const string Filename = "study-checkpoint.json";
    public StudyBinding Binding { get; } = binding;
    public DateTimeOffset StartedUtc { get; } = startedUtc;
    public double SetupSeconds { get; set; }
    public List<BlockAttempt> Attempts { get; init; } = [];
    public CheckpointRecovery? Recovery { get; init; }
    public bool Complete => Attempts.Count(x => x.State == BlockState.Complete) == Binding.Plan.Blocks;
    public int CompletedBlocks => Attempts.Count(x => x.State == BlockState.Complete);
    public int InvalidBlocks => Attempts.Count(x => x.State == BlockState.Invalid);
    public int Reruns => Attempts.Count(x => x.Attempt > 1);
    public double MeasuredMachineSeconds => SetupSeconds +
        Attempts.Sum(x => x.FailedMachineSeconds + x.Runs.Sum(r => r.MachineSeconds));
    public double BudgetChargeSeconds => Binding.Budget.PriorStudySeconds + MeasuredMachineSeconds +
        Attempts.Sum(x => x.UnmeasuredBudgetChargeSeconds);

    public BlockAttempt Start(int block)
    {
        if (Attempts.Any(x => x.State is BlockState.Running or BlockState.ObservedSafetyStop or BlockState.InstrumentationStop) ||
            block != CompletedBlocks + 1)
            throw new InvalidOperationException("A validated prior block or interruption disposition is required.");
        int previous = Attempts.Count(x => x.Block == block);
        if (InvalidBlocks > Binding.Budget.MaxInvalidBlocks)
            throw new InvalidDataException("The predeclared invalid-block rerun allowance has been exceeded.");
        var attempt = new BlockAttempt(block, previous + 1);
        Attempts.Add(attempt);
        return attempt;
    }

    public void EnsureBudgetForCell()
    {
        var settings = Binding.Plan.Settings;
        int duration = settings.WarmupSeconds + settings.MeasureSeconds + settings.DrainSeconds;
        if (BudgetChargeSeconds + duration > Binding.Budget.LimitSeconds)
            throw new InvalidDataException("Remaining machine time cannot cover the next planned cell.");
    }

    public async Task RecoverInterruptedAsync(string root, CancellationToken ct)
    {
        var unfinished = Attempts.LastOrDefault(x => x.State == BlockState.Running);
        if (unfinished is null) return;
        if (unfinished.Runs.Count == 16 && unfinished.ActiveSlot is null)
        {
            unfinished.State = BlockState.Complete;
            return;
        }
        var folder = Path.Combine(root, unfinished.Folder);
        var indexed = unfinished.Runs.Select(x => x.File).ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                string relative = $"{unfinished.Folder}/{Path.GetFileName(file)}";
                if (!indexed.Contains(relative))
                    unfinished.FailureEvidence.Add(new(relative, await EvidenceFiles.HashAsync(file, ct)));
            }
        }
        if (unfinished.ActiveSlot is not null)
            unfinished.UnmeasuredBudgetChargeSeconds = 180;
        unfinished.ActiveSlot = null;
        unfinished.State = BlockState.Invalid;
        unfinished.Reason = "process-interrupted-before-block-checkpoint";
    }

    public void ValidateStructure(bool complete)
    {
        var plan = Binding.Plan;
        if (!RunPlan.Create(plan.Phase, plan.Blocks, plan.WorkloadSeed, plan.OrderSeed, plan.Settings).Cells.SequenceEqual(plan.Cells) ||
            EvidenceFiles.HashJson(plan.Settings) != Binding.SettingsSha256)
            throw new InvalidDataException("The checkpoint schedule or settings differ from the plan.");
        int next = 1;
        int attemptForBlock = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Attempts.Count; i++)
        {
            var item = Attempts[i];
            if (item.Block != next || item.Block > plan.Blocks || item.Attempt != ++attemptForBlock ||
                item.Runs.Count > 16 || (item.State == BlockState.Complete && item.Runs.Count != 16) ||
                ((item.State is BlockState.Running or BlockState.ObservedSafetyStop or BlockState.InstrumentationStop) &&
                 i < Attempts.Count - 1) || (item.State == BlockState.Invalid && string.IsNullOrEmpty(item.Reason)))
                throw new InvalidDataException("Invalid or non-sequential block attempt state.");
            var scheduled = plan.Cells.Where(x => x.Block == item.Block).ToArray();
            for (int slot = 0; slot < item.Runs.Count; slot++)
            {
                var run = item.Runs[slot];
                var cell = scheduled[slot];
                if (run.Summary.Cell != cell || run.File != FileFor(item, slot, cell) || !ids.Add(run.Summary.RunId))
                    throw new InvalidDataException("A raw cell is duplicated or out of the registered sequence.");
            }
            if (item.State == BlockState.Complete)
            {
                next++;
                attemptForBlock = 0;
            }
        }
        if (complete && !Complete)
            throw new InvalidDataException("The study does not have every valid complete block.");
        if (Recovery is not null && (plan.Phase != "pilot" || Recovery.ResumedBuildSha256 != Binding.Identity.BuildSha256 ||
            Recovery.InvalidatedBlock != Recovery.RetainedCompleteBlocks + 1 ||
            !Attempts.Any(x => x.Block == Recovery.InvalidatedBlock && x.Attempt == 1 &&
                x.State == BlockState.Invalid && x.Reason == Recovery.Reason)))
            throw new InvalidDataException("The checkpoint recovery provenance is inconsistent.");
    }

    public static string FileFor(BlockAttempt attempt, int slot, RunCell cell) =>
        $"{attempt.Folder}/{slot + 1:D2}-{cell.Scenario}-{cell.Group}.json.gz";

    public static string UnderBlock(string root, BlockAttempt attempt, string file)
    {
        string name = Path.GetFileName(file);
        if (file != $"{attempt.Folder}/{name}" || name.Length == 0)
            throw new InvalidDataException("Evidence path escaped its block.");
        return Path.Combine(root, attempt.Folder, name);
    }
}
