namespace Capstone.Experiments;

internal sealed record FormalFreeze(string CodeCommit, StudyIdentity Identity, RunPlan Plan, string SettingsSha256,
    string PilotCheckpointSha256, string ScopeSha256, DateTimeOffset FrozenUtc, BudgetLimit Budget, StudyPrecision Precision)
{
    public static async Task<FormalFreeze> CreateAsync(string pilotRoot, int workloadSeed, int orderSeed,
        double setupAndArchiveSeconds, CancellationToken ct)
    {
        var pilot = await StudyRun.ReadAndVerifyAsync(pilotRoot, complete: true, ct);
        var precision = StudyPrecision.Calculate(pilot, setupAndArchiveSeconds);
        if (precision.SelectedBlocks is not int selected)
            throw new InvalidDataException("A formal plan cannot satisfy the registered budget or pilot reference requirement.");
        var plan = RunPlan.Create("formal", selected, workloadSeed, orderSeed, pilot.Binding.Plan.Settings);
        if (plan.Cells.Select(x => x.Seed).Intersect(pilot.Binding.Plan.Cells.Select(x => x.Seed)).Any())
            throw new InvalidDataException("Pilot and formal workload seeds must produce distinct blocks.");
        await using var database = Postgres.LabDatabase.FromEnvironment();
        await using var connection = await database.OpenConnectionAsync(ct);
        var now = await StudyIdentity.CaptureAsync(connection,
            new Npgsql.NpgsqlConnectionStringBuilder(database.ConnectionString).MaxPoolSize, ct);
        if (!pilot.Binding.Identity.SameExecutionAs(now))
            throw new InvalidDataException("Frozen execution identity must be the validated pilot source, build and runtime.");
        var pilotHash = await EvidenceFiles.HashAsync(Path.Combine(pilotRoot, StudyCheckpoint.Filename), ct);
        return new(now.ObservedHead, now, plan, EvidenceFiles.HashJson(plan.Settings), pilotHash, pilot.Binding.ScopeSha256,
            DateTimeOffset.UtcNow, new(86400 - precision.ReservedPostprocessingSeconds, precision.ChargedBeforeFormalSeconds,
                selected / 5), precision);
    }

    public void Validate()
    {
        if (Plan.Phase != "formal" || Precision.SelectedBlocks != Plan.Blocks ||
            SettingsSha256 != EvidenceFiles.HashJson(Plan.Settings) ||
            !RunPlan.Create("formal", Plan.Blocks, Plan.WorkloadSeed, Plan.OrderSeed, Plan.Settings).Cells.SequenceEqual(Plan.Cells) ||
            CodeCommit != Identity.ObservedHead || Budget.PriorStudySeconds != Precision.ChargedBeforeFormalSeconds ||
            Budget.LimitSeconds != 86400 - Precision.ReservedPostprocessingSeconds || Budget.MaxInvalidBlocks != Plan.Blocks / 5 ||
            !double.IsFinite(Budget.PriorStudySeconds) || Budget.PriorStudySeconds < 0 || Budget.LimitSeconds <= Budget.PriorStudySeconds)
            throw new InvalidDataException("The frozen plan is inconsistent.");
    }
}
