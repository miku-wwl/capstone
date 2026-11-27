using Capstone.Api;
using Capstone.Core;
using Capstone.Experiments;
using Capstone.Postgres;
using Xunit;

namespace Capstone.UnitTests;

public sealed class ExperimentProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pilot", 5, 80)]
    [InlineData("formal", 10, 160)]
    [InlineData("formal", 14, 224)]
    public void PlansAreSeedMatchedAndHaveNoMissingOrDuplicateCells(string phase, int blocks, int runs)
    {
        var plan = RunPlan.Create(phase, blocks, 19, 22, new());
        Assert.Equal(runs, plan.Cells.Length);
        foreach (var block in plan.Cells.GroupBy(x => x.Block))
        {
            Assert.Equal(16, block.Select(x => (x.Scenario, x.Group)).Distinct().Count());
            Assert.Single(block.Select(x => x.Seed).Distinct());
            var w2 = Workload.Generate(plan.Settings, block.First(x => x.Scenario == "W2"), 1, block.First().Seed);
            var w4 = Workload.Generate(plan.Settings, block.First(x => x.Scenario == "W4"), 1, block.First().Seed);
            Assert.Equal(w2, w4);
        }
    }

    [Fact]
    public void KnownPairedValuesUseSampleSdAndTheRegisteredMinimumBlockCount()
    {
        var result = StudyPrecision.CalculateFromPairs([0, 1, 2, 3, 4], 12, 123, 10_000);
        Assert.Equal(Math.Sqrt(2.5), result.SampleStandardDeviation, 9);
        Assert.Equal(1.2, result.TargetHalfWidth, 9);
        Assert.Equal(2.262157 * Math.Sqrt(2.5) / Math.Sqrt(10), result.Options[0].HalfWidth, 9);
        Assert.Equal(10, result.SelectedBlocks);
    }

    [Fact]
    public void ZeroReferenceBudgetAndExploratoryCasesAreDistinct()
    {
        Assert.Equal("reference-zero-investigate", StudyPrecision.CalculateFromPairs([0, 0, 0, 0, 0], 0, 120, 0).Status);
        Assert.Null(StudyPrecision.CalculateFromPairs([0, 1, 2, 3, 4], 10, 120, 85_000).SelectedBlocks);
        var exploratory = StudyPrecision.CalculateFromPairs([0, 100, 200, 300, 400], 10, 120, 10_000);
        Assert.Equal(10, exploratory.SelectedBlocks);
        Assert.Equal("exploratory", exploratory.Status);
    }

    [Fact]
    public async Task AtomicCheckpointPreservesInterruptedFilesAndRerunsTheWholeBlock()
    {
        var root = Directory.CreateTempSubdirectory("mse907-checkpoint-").FullName;
        try
        {
            var state = Checkpoint();
            var attempt = state.Start(1);
            attempt.ActiveSlot = 0;
            Directory.CreateDirectory(Path.Combine(root, attempt.Folder));
            string orphan = $"{attempt.Folder}/partial-raw";
            await File.WriteAllTextAsync(Path.Combine(root, orphan), "retained engineering test fixture", Ct);
            string path = Path.Combine(root, StudyCheckpoint.Filename);
            await EvidenceFiles.WriteAsync(path, state, replace: false, Ct);
            var resumed = await EvidenceFiles.ReadAsync<StudyCheckpoint>(path, Ct);
            await resumed.RecoverInterruptedAsync(root, Ct);
            Assert.Equal(BlockState.Invalid, resumed.Attempts.Single().State);
            Assert.Equal(orphan, resumed.Attempts.Single().FailureEvidence.Single().File);
            Assert.Equal(180, resumed.BudgetChargeSeconds);
            var retry = resumed.Start(1);
            Assert.Equal(2, retry.Attempt);
            Assert.NotEqual(attempt.Folder, retry.Folder);
            Assert.Equal(0, resumed.CompletedBlocks);
            await EvidenceFiles.WriteAsync(path, resumed, replace: true, Ct);
            (await EvidenceFiles.ReadAsync<StudyCheckpoint>(path, Ct)).ValidateStructure(complete: false);
            Assert.True(File.Exists(Path.Combine(root, orphan)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CheckpointPublicationSurvivesAConcurrentReadWithoutDeleteSharing()
    {
        var root = Directory.CreateTempSubdirectory("mse907-checkpoint-reader-").FullName;
        try
        {
            string path = Path.Combine(root, "checkpoint.json");
            await EvidenceFiles.WriteAsync(path, 1, replace: false, Ct);
            using var reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var next = EvidenceFiles.WriteAsync(path, 2, replace: true, Ct);
            await Task.Delay(250, Ct);
            reader.Dispose();
            await next;
            Assert.Equal(2, await EvidenceFiles.ReadAsync<int>(path, Ct));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SafetyObservationsCannotBeRemovedByRerunAndBudgetIsEnforced()
    {
        var state = Checkpoint();
        state.Start(1).State = BlockState.ObservedSafetyStop;
        Assert.Throws<InvalidOperationException>(() => state.Start(1));
        state.SetupSeconds = 86_300;
        Assert.Throws<InvalidDataException>(state.EnsureBudgetForCell);
        Assert.Equal(0, state.InvalidBlocks);
    }

    [Fact]
    public void DuplicatesAndLateAcknowledgementsHaveExclusiveOutcomesAndFixedDenominator()
    {
        var raw = Sample("W2", "A", (inputs, cell) =>
        {
            var first = inputs[0].Request;
            var repeat = first with { AttemptId = inputs[0].RepeatAttemptId };
            var next = inputs[1].Request;
            var last = inputs[2].Request;
            (TransferRequest request, Guid id)[] rows =
                [(first, Guid.NewGuid()), (repeat, Guid.NewGuid()), (next, Guid.NewGuid()), (last, Guid.NewGuid())];
            return ([new(inputs[0], 0, [Ack(rows[0], false, 30), Ack(rows[1], true, 34)]),
                     new(inputs[1], 250, [Ack(rows[2], false, 270)]),
                     new(inputs[2], 500, [Ack(rows[3], false, 3200)]),
                     new(inputs[3], 750, [], "capacity")], [], rows);
        });
        var s = ProtocolAudit.Summarize(raw);
        Assert.Empty(s.FinancialIssues);
        Assert.Equal(4, s.Offered);
        Assert.Equal(3, s.Dispatched);
        Assert.Equal(1, s.CapacityMisses);
        Assert.Equal(4, s.HttpAttempts);
        Assert.Equal(1, s.Outcomes.CorrectlyConfirmed);
        Assert.Equal(1, s.DuplicateLogical);
        Assert.Equal(1, s.ExtraFinancialEffects);
        Assert.Equal(1, s.Outcomes.Late);
        Assert.Equal(1, s.CorrectlyConfirmedTps);
        Assert.NotNull(s.P95Ms);
    }

    [Fact]
    public void UnmatchedAcknowledgementDoesNotDiscardUnrelatedCorrectConfirmations()
    {
        var raw = Sample("W4", "C", (inputs, cell) =>
        {
            (TransferRequest, Guid) valid = (inputs[0].Request, Guid.NewGuid());
            return ([new(inputs[0], 0, [Ack(valid, false, 40)]),
                     new(inputs[1], 250, [Ack((inputs[1].Request, Guid.NewGuid()), false, 300)]),
                     new(inputs[2], 500, [new(inputs[2].Request, false, 500, 510, null, null, "http-transport")]),
                     new(inputs[3], 750, [], "window")], [], [valid]);
        });
        var s = ProtocolAudit.Summarize(raw);
        Assert.Equal(1, s.Outcomes.CorrectlyConfirmed);
        Assert.Equal(1, s.Outcomes.ClientMismatch);
        Assert.Equal(1, s.Outcomes.Unknown);
        Assert.Equal(1, s.Exposure.OtherTransportFailures);
        Assert.Equal(0, s.Exposure.InjectedTransportLosses);
        Assert.Equal(0, s.Exposure.Repeated);
    }

    [Fact]
    public void NoValidConfirmationsLeavesPercentilesUndefined()
    {
        var raw = Sample("W1-high", "B", (inputs, _) =>
            (inputs.Select(i => new LogicalRun(i, 0, [], "window")).ToArray(), [], []));
        var result = ProtocolAudit.Summarize(raw);
        Assert.Null(result.P95Ms);
        Assert.Null(result.UnknownRate);
        Assert.Equal(0, result.CorrectlyConfirmedTps);
    }

    private static StudyCheckpoint Checkpoint()
    {
        var plan = RunPlan.Create("pilot", 5, 17, 29, new());
        var identity = new StudyIdentity("test", "source", "build", [], [], new("runtime", "os", "cpu", 4, "17.7", "image", "vm", 12));
        return new(new(plan, EvidenceFiles.HashJson(plan.Settings), "scope", null, identity, new(86400, 0, 1)), DateTimeOffset.UtcNow);
    }

    private static HttpAttempt Ack((TransferRequest request, Guid id) entry, bool repeat, double ended) =>
        new(entry.request, repeat, Math.Max(0, ended >= 3000 ? 500 : ended - 20), ended, 200,
            new TransferReply(TransferStatus.Committed, entry.id), null);

    private static RunEvidence Sample(string scenario, string group,
        Func<LogicalInput[], RunCell, (LogicalRun[] Requests, ServerAttempt[] Server, (TransferRequest Request, Guid Id)[] Rows)> build)
    {
        var settings = new WorkloadSettings(Accounts: 4, OfferedPerSecond: 4, MeasureSeconds: 1,
            DrainSeconds: 2, AssignedRepeatProbability: 1);
        var cell = new RunCell(1, scenario, group, 117);
        var inputs = Workload.Generate(settings, cell, 1, cell.Seed);
        var initial = LabDatabase.MakeSeed(cell.Seed, settings.Accounts);
        var x = build(inputs, cell);
        var transfers = x.Rows.Select(t => new TransferSnapshot(t.Id, t.Request.LogicalRequestId, t.Request.AttemptId,
            t.Request.SourceAccount, t.Request.DestinationAccount, t.Request.Amount, t.Request.Currency)).ToArray();
        var entries = transfers.SelectMany(t => new EntrySnapshot[]
        {
            new(t.TransferId, t.SourceAccount, "debit", -t.Amount), new(t.TransferId, t.DestinationAccount, "credit", t.Amount)
        }).ToArray();
        var accounts = initial.Select(a => new AccountSnapshot(a.Id,
            a.Balance + entries.Where(e => e.AccountId == a.Id).Sum(e => e.SignedAmount), 0, true)).ToArray();
        return new("fixture", DateTimeOffset.UtcNow, cell, settings, initial,
            new(3000, false, x.Requests, x.Server, [new(10, 0, 1024, 1, 0, 0)]),
            new(accounts, transfers, entries), 4, VerifiedResetSha256: EvidenceFiles.HashJson(initial));
    }
}
