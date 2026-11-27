using Capstone.Core;

namespace Capstone.Experiments;

internal sealed record WorkloadSettings(
    int Accounts = 1000,
    int OfferedPerSecond = 120,
    int MaxInFlight = 48,
    int WarmupSeconds = 30,
    int MeasureSeconds = 60,
    int DrainSeconds = 30,
    double HotAccountFraction = .01,
    double LowHotProbability = .1,
    double HighHotProbability = .8,
    double AssignedRepeatProbability = .2,
    int MaxTransactionRetries = 3,
    int LockTimeoutMs = 1500,
    int AttemptTimeoutMs = 6000,
    int InitialBackoffMs = 5,
    int BackoffCapMs = 50)
{
    public void Validate(bool study)
    {
        if (Accounts is < 4 or > 100_000 || OfferedPerSecond is < 1 or > 2_000 ||
            MaxInFlight is < 1 or > 512 || WarmupSeconds is < 1 or > 30 ||
            MeasureSeconds is < 1 or > 60 || DrainSeconds is < 1 or > 30 ||
            !Probability(HotAccountFraction) || HotAccountFraction == 0 || !Probability(LowHotProbability) ||
            !Probability(HighHotProbability) || HighHotProbability <= LowHotProbability ||
            !Probability(AssignedRepeatProbability) || AssignedRepeatProbability == 0 ||
            MaxTransactionRetries is < 0 or > 3 || LockTimeoutMs is < 1 or > 5000 ||
            AttemptTimeoutMs is < 1 or > 10000 || InitialBackoffMs is < 0 or > 50 ||
            BackoffCapMs < InitialBackoffMs || BackoffCapMs > 500)
            throw new ArgumentException("A workload parameter is outside its supported bounds.");
        if (study && (WarmupSeconds != 30 || MeasureSeconds != 60 || DrainSeconds != 30))
            throw new ArgumentException("Study runs require the registered 30/60/30-second windows.");
    }

    private static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}

internal sealed record RunCell(int Block, string Scenario, string Group, int Seed);

internal sealed record RunPlan(string Phase, int Blocks, int WorkloadSeed, int OrderSeed,
    WorkloadSettings Settings, RunCell[] Cells)
{
    public static readonly string[] Scenarios = ["W1-low", "W1-high", "W2", "W4"];
    public static readonly string[] Groups = ["A", "B", "C", "D"];

    public static RunPlan Create(string phase, int blocks, int seed, int orderSeed, WorkloadSettings settings)
    {
        if (phase switch { "engineering" => blocks != 1, "pilot" => blocks != 5, "formal" => blocks is < 10 or > 14, _ => true })
            throw new ArgumentException("Use one engineering block, five pilot blocks, or 10–14 formal blocks.");
        settings.Validate(study: phase != "engineering");
        var workloadSeeds = new Random(seed);
        var order = new Random(orderSeed);
        var cells = new List<RunCell>();
        for (int block = 1; block <= blocks; block++)
        {
            int matchingSeed = workloadSeeds.Next();
            var members = Scenarios.SelectMany(scenario =>
                Groups.Select(group => new RunCell(block, scenario, group, matchingSeed))).ToArray();
            order.Shuffle(members.AsSpan());
            cells.AddRange(members);
        }
        return new(phase, blocks, seed, orderSeed, settings, cells.ToArray());
    }
}

internal sealed record LogicalInput(int Order, TransferRequest Request, Guid RepeatAttemptId, bool RepeatAssigned);

internal static class Workload
{
    public static LogicalInput[] Generate(WorkloadSettings settings, RunCell cell, int seconds, int seed)
    {
        var rng = new Random(seed);
        int hot = Math.Clamp((int)(settings.Accounts * settings.HotAccountFraction), 1, settings.Accounts - 1);
        double hotProbability = cell.Scenario == "W1-low" ? settings.LowHotProbability : settings.HighHotProbability;
        var plan = new LogicalInput[settings.OfferedPerSecond * seconds];
        Span<byte> ids = stackalloc byte[48];
        for (int i = 0; i < plan.Length; i++)
        {
            int debit = rng.NextDouble() < hotProbability ? rng.Next(1, hot + 1) : rng.Next(hot + 1, settings.Accounts + 1);
            int credit = rng.Next(1, settings.Accounts);
            if (credit >= debit)
                credit++;
            decimal amount = rng.Next(1, 501) / 100m;
            double assignedDraw = rng.NextDouble();
            bool assigned = cell.Scenario is "W2" or "W4" && assignedDraw < settings.AssignedRepeatProbability;
            rng.NextBytes(ids);
            var request = new TransferRequest(new Guid(ids[..16]), new Guid(ids[16..32]), debit, credit, amount, "NZD");
            plan[i] = new(i, request, new Guid(ids[32..]), assigned);
        }
        return plan;
    }
}
