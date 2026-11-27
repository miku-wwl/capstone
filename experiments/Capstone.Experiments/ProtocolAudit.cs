using Capstone.Core;
using Capstone.Postgres;

namespace Capstone.Experiments;

internal sealed record OutcomePartition(int CorrectlyConfirmed, int Duplicate, int ClientMismatch, int Unreconciled,
    int Late, int Unknown, int BusinessRejected, int OperationalFailure);

internal sealed record Exposure(int Assigned, int AssignedDispatched, int Eligible, int Triggered, int Repeated,
    int ServerOverlap, int InjectedTransportLosses, int OtherTransportFailures);

internal sealed record ProtocolSummary(
    string RunId, RunCell Cell, int Offered, int Dispatched, int CapacityMisses, int WindowMisses, int HttpAttempts,
    double HttpAttemptTps, double CorrectlyConfirmedTps, OutcomePartition Outcomes,
    int OnTimeUniqueMatched, double? P50Ms, double? P95Ms, double? P99Ms, double? P95DispatchLagMs,
    int DuplicateLogical, int ExtraFinancialEffects, int ZeroEffectLogical, int OneEffectLogical,
    int CommittedEffects, double? BusinessRejectionRate, double? UnknownRate, double? FailureRate,
    int LateConfirmations, int ClientLedgerMismatch, bool ClientActiveAtCutoff, int HotDebits,
    Exposure Exposure, int TransactionAttempts, int InternalRetries, int VersionConflicts,
    int LockTimeouts, int Deadlocks, int KeyBusy, int Replays, int RetryExhausted,
    int ResourceSamples, int PeakDbSessions, int PeakLockWaiters, int LockWaiterSamples,
    long PeakWorkingSetBytes, double? SampledProcessCpuSeconds,
    string[] FinancialIssues, string[] ClientIssues, string[] InstrumentationIssues,
    bool PersistentKeyViolation, CellTiming? Timing, double ElapsedSeconds);

internal static class ProtocolAudit
{
    public static ProtocolSummary Summarize(RunEvidence run)
    {
        var window = run.Measurement;
        var all = window.LogicalRequests;
        var dispatched = all.Where(x => x.Attempts.Length != 0).ToArray();
        var attempts = dispatched.SelectMany(x => x.Attempts).ToArray();
        var traces = window.ServerAttempts.ToLookup(x => x.Attempt.Request.AttemptId);
        var ids = attempts.Select(x => x.Request.AttemptId).ToHashSet();
        var telemetry = new List<string>();
        if (ids.Count != attempts.Length || traces.Any(tr => !ids.Contains(tr.Key) || tr.Count() != 1))
            telemetry.Add("attempt-trace-identity");
        if (all.Length != run.Settings.OfferedPerSecond * run.Settings.MeasureSeconds ||
            all.Select(x => x.Input.Order).Distinct().Count() != all.Length)
            telemetry.Add("offered-schedule-cardinality");
        if (run.VerifiedResetSha256 != EvidenceFiles.HashJson(run.Initial) ||
            !run.Initial.SequenceEqual(LabDatabase.MakeSeed(run.Cell.Seed, run.Settings.Accounts)))
            telemetry.Add("initial-state-fingerprint");
        var generated = Workload.Generate(run.Settings, run.Cell, run.Settings.MeasureSeconds, run.Cell.Seed);
        if (!all.Select(x => x.Input).SequenceEqual(generated) ||
            dispatched.Any(x => x.Attempts[0].SentMs >= run.Settings.MeasureSeconds * 1000d))
            telemetry.Add("dispatch-cohort-or-input");
        if (window.CutoffMs != (run.Settings.MeasureSeconds + run.Settings.DrainSeconds) * 1000d ||
            window.Resources.Length == 0)
            telemetry.Add("window-or-resources");
        int capacity = all.Count(x => x.Miss == "capacity" && x.Attempts.Length == 0);
        int missedWindow = all.Count(x => x.Miss == "window" && x.Attempts.Length == 0);
        if (capacity + missedWindow + dispatched.Length != all.Length)
            telemetry.Add("dispatch-partition");

        var audit = LedgerAuditor.Reconcile(run.Initial,
            attempts.DistinctBy(a => a.Request.AttemptId).Select(a => a.Request).ToArray(), run.Ledger);
        var transfers = run.Ledger.Transfers.ToDictionary(x => x.TransferId);
        var clientIssues = new List<string>();
        var latencies = new List<double>();
        var dispatchLag = new List<double>();
        int duplicated = 0, extra = 0, zero = 0, one = 0, hot = 0;
        int mismatch = 0, late = 0, onTime = 0, unknown = 0, business = 0, operational = 0, unreconciled = 0;
        int assignedDispatched = 0, eligible = 0, triggered = 0, repeated = 0, overlap = 0, injected = 0, other = 0;
        int inclusiveLate = 0;
        int hotAccounts = Math.Clamp((int)(run.Settings.Accounts * run.Settings.HotAccountFraction), 1, run.Settings.Accounts - 1);

        foreach (var item in dispatched)
        {
            var first = item.Attempts[0];
            var input = item.Input.Request;
            if (first.Repeat || first.Request != input || item.Attempts.Length > 2 ||
                item.Attempts.Skip(1).Any(a => !a.Repeat || a.Request != (input with { AttemptId = item.Input.RepeatAttemptId })))
                telemetry.Add("attempt-payload-or-repeat");
            if (input.SourceAccount <= hotAccounts) hot++;
            dispatchLag.Add(first.SentMs - item.ScheduledMs);
            int effects = audit.EffectsByLogicalRequest.GetValueOrDefault(input.LogicalRequestId);
            if (effects == 0) zero++;
            if (effects == 1) one++;
            if (effects > 1)
            {
                duplicated++;
                extra += effects - 1;
            }

            double? confirmed = null;
            bool inconsistent = false;
            foreach (var attempt in item.Attempts)
            {
                if (attempt.Reply?.Status != TransferStatus.Committed)
                    continue;
                if (attempt.HttpStatus != 200 || attempt.Reply.TransferId is not Guid id ||
                    !transfers.TryGetValue(id, out var transfer) || transfer.LogicalRequestId != input.LogicalRequestId)
                {
                    inconsistent = true;
                    clientIssues.Add($"acknowledgement-without-matching-effect:{attempt.Request.AttemptId:D}");
                }
                else
                    confirmed = Math.Min(confirmed ?? double.PositiveInfinity, attempt.EndedMs);
            }
            bool uniqueOnTime = effects == 1 && confirmed <= window.CutoffMs && !inconsistent;
            if (uniqueOnTime) onTime++;
            if (confirmed > window.CutoffMs) inclusiveLate++;
            if (effects > 1) { } // Duplicates have precedence in the mutually exclusive outcome partition.
            else if (inconsistent) mismatch++;
            else if (uniqueOnTime)
            {
                if (audit.Reconciled)
                    latencies.Add(confirmed.GetValueOrDefault() - first.SentMs);
                else
                    unreconciled++;
            }
            else if (confirmed > window.CutoffMs) late++;
            else if (effects == 1 || item.Attempts.Any(x =>
                         x.TransportFailure is not null || x.Reply?.Status == TransferStatus.UnknownCommit)) unknown++;
            else if (item.Attempts.Any(x => x.Reply?.Status is TransferStatus.AccountUnavailable or
                         TransferStatus.InsufficientFunds or TransferStatus.KeyPayloadMismatch or
                         TransferStatus.InvalidKey or TransferStatus.InvalidRequest)) business++;
            else operational++;

            int transportFailures = item.Attempts.Count(a => a.TransportFailure is not null);
            other += transportFailures;
            repeated += item.Attempts.Count(a => a.Repeat);
            if (!item.Input.RepeatAssigned) continue;
            assignedDispatched++;
            var firstTrace = traces[first.Request.AttemptId].FirstOrDefault();
            if (run.Cell.Scenario == "W2")
            {
                eligible++;
                if (item.Attempts.Length == 2)
                {
                    var repeat = item.Attempts[1];
                    var repeatTrace = traces[repeat.Request.AttemptId].FirstOrDefault();
                    if (firstTrace is not null && repeatTrace is not null && repeat.SentMs <= first.EndedMs)
                        triggered++;
                    if (firstTrace?.AdmittedMs is double firstAt && repeatTrace?.AdmittedMs is double repeatAt &&
                        Math.Max(firstAt, repeatAt) <= Math.Min(firstTrace.ObservedMs, repeatTrace.ObservedMs))
                        overlap++;
                }
            }
            else if (run.Cell.Scenario == "W4" && firstTrace?.Attempt.Result is { Status: TransferStatus.Committed, Replayed: false })
            {
                eligible++;
                if (firstTrace.Attempt.ResponseDropped)
                {
                    triggered++;
                    if (first.TransportFailure == "http-transport")
                    {
                        injected++;
                        other--;
                    }
                }
            }
            if (run.Cell.Scenario == "W4" && item.Attempts.Length == 2 &&
                !(firstTrace?.Attempt.ResponseDropped == true && first.TransportFailure == "http-transport"))
                telemetry.Add("unattributed-response-loss-retry");
        }
        var results = window.ServerAttempts.Select(x => x.Attempt.Result).ToArray();
        var resources = window.Resources;
        int confirmedCount = latencies.Count;
        return new(run.RunId, run.Cell, all.Length, dispatched.Length, capacity, missedWindow, attempts.Length,
            (double)attempts.Length / run.Settings.MeasureSeconds, (double)confirmedCount / run.Settings.MeasureSeconds,
            new(confirmedCount, duplicated, mismatch, unreconciled, late, unknown, business, operational),
            onTime, Quantile(latencies, .50), Quantile(latencies, .95), Quantile(latencies, .99), Quantile(dispatchLag, .95),
            duplicated, extra, zero, one, run.Ledger.Transfers.Count, Rate(business), Rate(unknown), Rate(business + operational),
            inclusiveLate, clientIssues.Count, window.ClientActiveAtCutoff, hot,
            new(all.Count(x => x.Input.RepeatAssigned), assignedDispatched, eligible, triggered, repeated, overlap, injected, other),
            results.Sum(x => x.TransactionAttempts), results.Sum(x => Math.Max(0, x.TransactionAttempts - 1)),
            results.Sum(x => x.VersionConflicts), results.Sum(x => x.LockTimeouts), results.Sum(x => x.Deadlocks),
            results.Count(x => x.Status == TransferStatus.KeyBusy), results.Count(x => x.Replayed),
            results.Count(x => x.Status == TransferStatus.RetryExhausted), resources.Length,
            resources.Select(x => x.DatabaseSessions).DefaultIfEmpty().Max(), resources.Select(x => x.LockWaiters).DefaultIfEmpty().Max(),
            resources.Count(x => x.LockWaiters > 0), resources.Select(x => x.ProcessWorkingSetBytes).DefaultIfEmpty().Max(),
            resources.Length > 1 ? resources[^1].ProcessCpuSeconds - resources[0].ProcessCpuSeconds : null,
            audit.Violations.ToArray(), clientIssues.ToArray(), telemetry.Distinct().ToArray(),
            (run.Cell.Group is "C" or "D") && duplicated != 0, run.Timing, run.ElapsedSeconds);

        double? Rate(int n) => dispatched.Length == 0 ? null : (double)n / dispatched.Length;
    }

    private static double? Quantile(List<double> values, double p)
    {
        if (values.Count == 0) return null;
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * p) - 1];
    }
}
