using Capstone.Api;
using Capstone.Core;
using Capstone.Postgres;

namespace Capstone.Experiments;

internal sealed record HttpAttempt(TransferRequest Request, bool Repeat, double SentMs, double EndedMs,
    int? HttpStatus, TransferReply? Reply, string? TransportFailure);

internal sealed record LogicalRun(LogicalInput Input, double ScheduledMs, HttpAttempt[] Attempts);

internal sealed record ServerAttempt(double ObservedMs, ApiAttempt Attempt);

internal sealed record RuntimeSample(double ElapsedMs, double ProcessCpuSeconds, long ProcessWorkingSetBytes,
    int DatabaseSessions, int ActiveQueries, int LockWaiters);

internal sealed record WindowEvidence(double CutoffMs, bool ClientActiveAtCutoff,
    LogicalRun[] LogicalRequests, ServerAttempt[] ServerAttempts, RuntimeSample[] Resources);

internal sealed record RunEvidence(string RunId, DateTimeOffset StartedUtc, RunCell Cell, WorkloadSettings Settings,
    IReadOnlyList<SeedAccount> Initial, WindowEvidence Measurement, LedgerSnapshot Ledger, double ElapsedSeconds);

internal sealed record RunSummary(
    string RunId, RunCell Cell, int Planned, int Dispatched, int CapacityOrWindowMisses,
    int HttpAttempts, double HttpAttemptsPerSecond, int CorrectlyConfirmed, double CorrectlyConfirmedTps,
    double? P50Ms, double? P95Ms, double? P99Ms, double? P95DispatchLagMs,
    int DuplicateLogical, int ExtraEffects, int Failed, int Unknown, int ConfirmedAfterCutoff,
    double? FailureRate, double? UnknownRate, int Assigned, int Eligible, int Triggered, int Repeats,
    int TransactionAttempts, int InternalRetries, int VersionConflicts, int LockTimeouts, int Deadlocks,
    int KeyReplays, int KeyBusy, int RetryExhausted, int CommittedEffects, bool ClientActiveAtCutoff,
    string[] AuditViolations, int PeakSampledDbSessions, int PeakSampledLockWaiters,
    long PeakWorkingSetBytes, double? SampledProcessCpuSeconds, double ElapsedSeconds);

internal static class RunAudit
{
    public static RunSummary Summarize(RunEvidence run)
    {
        var measurement = run.Measurement;
        var logical = measurement.LogicalRequests;
        var attempts = logical.SelectMany(x => x.Attempts).ToArray();
        var traceByAttempt = measurement.ServerAttempts
            .ToDictionary(x => x.Attempt.Request.AttemptId, x => x.Attempt);
        var audit = LedgerAuditor.Reconcile(run.Initial, attempts.Select(x => x.Request).ToArray(), run.Ledger);
        var violations = audit.Violations.ToList();
        var transfersById = run.Ledger.Transfers.ToDictionary(x => x.TransferId);
        var latencies = new List<double>();
        var dispatchLag = new List<double>();
        int failed = 0, unknown = 0, late = 0, eligible = 0, triggered = 0, duplicates = 0, extra = 0;

        foreach (var item in logical.Where(x => x.Attempts.Length > 0))
        {
            var first = item.Attempts[0];
            dispatchLag.Add(first.SentMs - item.ScheduledMs);
            int effects = audit.EffectsByLogicalRequest.GetValueOrDefault(item.Input.Request.LogicalRequestId);
            if (effects > 1)
            {
                duplicates++;
                extra += effects - 1;
                if (run.Cell.Group is "C" or "D")
                    violations.Add($"Persistent-key group produced multiple effects for {first.Request.LogicalRequestId}.");
            }

            double? confirmedMs = null;
            foreach (var attempt in item.Attempts.Where(a => a.Reply?.Status == TransferStatus.Committed))
            {
                if (attempt.HttpStatus != 200 || attempt.Reply?.TransferId is not Guid transferId ||
                    !transfersById.TryGetValue(transferId, out var transfer) ||
                    transfer.LogicalRequestId != first.Request.LogicalRequestId)
                    violations.Add($"Confirmation {attempt.Request.AttemptId} could not be matched to the ledger.");
                else
                    confirmedMs = Math.Min(confirmedMs ?? double.PositiveInfinity, attempt.EndedMs);
            }
            bool uncertain = item.Attempts.Any(a => a.Reply?.Status == TransferStatus.UnknownCommit ||
                (a.TransportFailure is not null && a.Reply is null)) || effects > 0;
            if (effects == 1 && confirmedMs is double at && at <= measurement.CutoffMs)
                latencies.Add(at - first.SentMs);
            else if (confirmedMs > measurement.CutoffMs)
                late++;
            else if (effects <= 1 && uncertain)
                unknown++;
            else if (effects <= 1)
                failed++;

            if (!item.Input.RepeatAssigned)
                continue;
            traceByAttempt.TryGetValue(first.Request.AttemptId, out var firstTrace);
            bool repeatIssued = item.Attempts.Length == 2;
            if (run.Cell.Scenario == "W2" && repeatIssued)
            {
                eligible++;
                var repeat = item.Attempts[1];
                if (firstTrace is not null && traceByAttempt.ContainsKey(repeat.Request.AttemptId) &&
                    repeat.SentMs <= first.EndedMs)
                    triggered++;
            }
            if (run.Cell.Scenario == "W4" && firstTrace is { Result.Status: TransferStatus.Committed,
                    Result.Replayed: false })
            {
                eligible++;
                if (firstTrace.ResponseDropped)
                    triggered++;
            }
        }

        if (violations.Count > 0)
            latencies.Clear();
        var serverResults = measurement.ServerAttempts.Select(x => x.Attempt.Result).ToArray();
        var resources = measurement.Resources;
        int dispatched = dispatchLag.Count;
        return new(run.RunId, run.Cell, logical.Length, dispatched, logical.Length - dispatched, attempts.Length,
            (double)attempts.Length / run.Settings.MeasureSeconds,
            latencies.Count, (double)latencies.Count / run.Settings.MeasureSeconds,
            Quantile(latencies, .50), Quantile(latencies, .95), Quantile(latencies, .99),
            Quantile(dispatchLag, .95), duplicates, extra, failed, unknown, late,
            Rate(failed, dispatched), Rate(unknown, dispatched), logical.Count(x => x.Input.RepeatAssigned),
            eligible, triggered, attempts.Count(a => a.Repeat), serverResults.Sum(r => r.TransactionAttempts),
            serverResults.Sum(r => Math.Max(0, r.TransactionAttempts - 1)), serverResults.Sum(r => r.VersionConflicts),
            serverResults.Sum(r => r.LockTimeouts), serverResults.Sum(r => r.Deadlocks),
            serverResults.Count(r => r.Replayed), serverResults.Count(r => r.Status == TransferStatus.KeyBusy),
            serverResults.Count(r => r.Status == TransferStatus.RetryExhausted), run.Ledger.Transfers.Count,
            measurement.ClientActiveAtCutoff, violations.ToArray(),
            resources.Select(x => x.DatabaseSessions).DefaultIfEmpty().Max(),
            resources.Select(x => x.LockWaiters).DefaultIfEmpty().Max(),
            resources.Select(x => x.ProcessWorkingSetBytes).DefaultIfEmpty().Max(),
            resources.Length > 1 ? resources[^1].ProcessCpuSeconds - resources[0].ProcessCpuSeconds : null,
            run.ElapsedSeconds);
    }

    private static double? Rate(int count, int denominator) => denominator == 0 ? null : (double)count / denominator;

    private static double? Quantile(List<double> values, double probability)
    {
        if (values.Count == 0)
            return null;
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * probability) - 1];
    }
}
