namespace Capstone.Experiments;

internal sealed record PrecisionOption(int FormalBlocks, int FormalRuns, double ProjectedHalfWidth);
internal sealed record PilotPrecision(double[] PairedInteractionDifferences, double ReferenceMeanTps,
    double TargetHalfWidth, double SampleStandardDeviation, int? SmallestEligibleBlockCount,
    PrecisionOption[] Options, string Status)
{
    // Two-sided 95% Student-t critical values for 9–13 degrees of freedom.
    private static readonly double[] CriticalValues = [2.262157, 2.228139, 2.200985, 2.178813, 2.160369];

    public static PilotPrecision Evaluate(ExperimentManifest manifest)
    {
        if (manifest.Plan.Phase != "pilot" || manifest.Plan.Blocks != 5 || manifest.Runs.Length != 80 ||
            string.IsNullOrWhiteSpace(manifest.DecisionSha256))
            throw new InvalidDataException("Precision assessment requires a complete study pilot with verified raw evidence.");
        var summaries = manifest.Runs.Select(run => run.Summary)
            .ToDictionary(cell => (cell.Cell.Block, cell.Cell.Scenario, cell.Cell.Group));
        var blocks = Enumerable.Range(1, 5).ToArray();
        var paired = blocks.Select(block => Interaction(block, "W4") - Interaction(block, "W2")).ToArray();
        double reference = blocks.Average(block => Tps(block, "W1-high", "A"));
        double target = .10 * reference;
        double average = paired.Average();
        double deviation = Math.Sqrt(paired.Sum(x => (x - average) * (x - average)) / (paired.Length - 1));
        var options = CriticalValues.Select((critical, index) =>
        {
            int count = index + 10;
            return new PrecisionOption(count, count * 16, critical * deviation / Math.Sqrt(count));
        }).ToArray();
        int? suggested = reference > 0 ? options.FirstOrDefault(x => x.ProjectedHalfWidth <= target)?.FormalBlocks : null;
        return new(paired, reference, target, deviation, suggested, options,
            reference == 0 ? "reference-zero-investigate" : suggested is null ? "exploratory" : "precision-target-provisionally-met");

        double Tps(int block, string scenario, string group) => summaries[(block, scenario, group)].CorrectlyConfirmedTps;
        double Interaction(int block, string scenario) =>
            (Tps(block, scenario, "D") - Tps(block, scenario, "C")) -
            (Tps(block, scenario, "B") - Tps(block, scenario, "A"));
    }
}
