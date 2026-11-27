namespace Capstone.Experiments;

internal sealed record PilotBudgetOption(int Blocks, int FormalRuns, int ReservedRerunRuns, double HalfWidth,
    double ProjectedTotalSeconds, bool MeetsPrecision, bool WithinBudget);

internal sealed record StudyPrecision(double[] PairedInteractionDifferences, double ReferenceTps, double TargetHalfWidth,
    double SampleStandardDeviation, double SecondsPerRun, double ChargedBeforeFormalSeconds,
    double ReservedPostprocessingSeconds, PilotBudgetOption[] Options, int? SelectedBlocks, string Status)
{
    private static readonly double[] Critical95 = [2.262157, 2.228139, 2.200985, 2.178813, 2.160369];

    public static StudyPrecision Calculate(StudyCheckpoint pilot, double prepSeconds)
    {
        if (pilot.Binding.Plan.Phase != "pilot" || !pilot.Complete ||
            !double.IsFinite(prepSeconds) || prepSeconds < 0)
            throw new InvalidDataException("Precision requires a verified five-block study pilot and valid preparation time.");
        var observed = pilot.Attempts.Where(x => x.State == BlockState.Complete).ToArray();
        var runs = observed.SelectMany(x => x.Runs).ToArray();
        var cells = runs.ToDictionary(x => (x.Summary.Cell.Block, x.Summary.Cell.Scenario, x.Summary.Cell.Group),
            x => x.Summary.CorrectlyConfirmedTps);
        double[] delta = observed.Select(x => Interaction(x.Block, "W4") - Interaction(x.Block, "W2")).ToArray();
        double reference = observed.Average(x => Tps(x.Block, "W1-high", "A"));
        return CalculateFromPairs(delta, reference, runs.Average(r => r.MachineSeconds),
            pilot.BudgetChargeSeconds + prepSeconds);

        double Tps(int block, string scenario, string group) => cells[(block, scenario, group)];
        double Interaction(int block, string scenario) =>
            Tps(block, scenario, "D") - Tps(block, scenario, "C") - Tps(block, scenario, "B") + Tps(block, scenario, "A");
    }

    internal static StudyPrecision CalculateFromPairs(double[] delta, double reference, double secondsPerRun, double spent)
    {
        if (delta.Length != 5 || !delta.All(double.IsFinite) || !double.IsFinite(reference) || reference < 0 ||
            !double.IsFinite(secondsPerRun) || secondsPerRun <= 0 || !double.IsFinite(spent) || spent < 0)
            throw new ArgumentException("Invalid data for the registered precision calculation.");
        double target = reference * .1;
        double mean = delta.Average();
        double sd = Math.Sqrt(delta.Sum(x => (x - mean) * (x - mean)) / (delta.Length - 1));
        const double reserve = 3 * 3600; // The protocol reserves two hours for archive/audit and one for analysis.
        var options = Critical95.Select((t, i) =>
        {
            int n = i + 10;
            int rerun = n / 5 * 16;
            double half = t * sd / Math.Sqrt(n);
            double projected = spent + reserve + (n * 16 + rerun) * Math.Max(120, secondsPerRun);
            return new PilotBudgetOption(n, n * 16, rerun, half, projected, reference > 0 && half <= target, projected <= 86400);
        }).ToArray();
        int? precise = options.FirstOrDefault(x => x.WithinBudget && x.MeetsPrecision)?.Blocks;
        int? feasible = options.FirstOrDefault(x => x.WithinBudget)?.Blocks;
        return new(delta, reference, target, sd, secondsPerRun, spent, reserve, options,
            reference == 0 ? null : precise ?? feasible,
            reference == 0 ? "reference-zero-investigate" : precise.HasValue ? "confirmatory-candidate" :
            feasible.HasValue ? "exploratory" : "budget-insufficient");
    }
}
