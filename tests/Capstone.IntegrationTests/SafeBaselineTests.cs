using Capstone.Core;
using Capstone.Postgres;
using Npgsql;
using Xunit;

namespace Capstone.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class SafeBaselineTests : IAsyncLifetime
{
    private readonly NpgsqlDataSource source = LabDatabase.FromEnvironment();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await new LabDatabase(source).MigrateAsync(Ct);

    public ValueTask DisposeAsync() => source.DisposeAsync();

    [Theory]
    [InlineData(30, 2, 40)]
    [InlineData(60, 1, 40)]
    public async Task T1_ConcurrentDebitsUseTheCurrentRow(int amount, int expectedCommits, int expectedBalance)
    {
        SeedAccount[] initial = [new(1, 100m), new(2, 0m), new(3, 0m)];
        await new LabDatabase(source).ResetAsync(initial, Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ready = 0;
        var service = new TransferService(source, async (step, _, ct) =>
        {
            if (step == TransferStep.BeforeAccountWrites)
            {
                if (Interlocked.Increment(ref ready) == 2)
                    staged.SetResult();
                await staged.Task.WaitAsync(ct);
            }
        });
        TransferRequest[] inputs = [Create(1, 2, amount), Create(1, 3, amount)];
        var receipts = await Task.WhenAll(inputs.Select(x => service.ExecuteAsync(x, timeout.Token)));
        Assert.Equal(expectedCommits, receipts.Count(x => x.Status == TransferStatus.Committed));
        Assert.Equal(2 - expectedCommits, receipts.Count(x => x.Status == TransferStatus.InsufficientFunds));

        var snapshot = await new LedgerAuditor(source).ReadSnapshotAsync(Ct);
        var audit = LedgerAuditor.Reconcile(initial, inputs, snapshot);
        Assert.True(audit.Reconciled, string.Join(Environment.NewLine, audit.Violations));
        Assert.Equal((decimal)expectedBalance, snapshot.Accounts[0].Balance);
        Assert.Equal(2 * expectedCommits, snapshot.Entries.Count);
        Assert.All(receipts.Where(x => x.Status == TransferStatus.Committed),
            receipt => Assert.Contains(snapshot.Transfers, x => x.TransferId == receipt.TransferId));
    }

    [Fact]
    public async Task T2_InsufficientFundsLeaveNoBusinessEffects()
    {
        SeedAccount[] initial = [new(1, 100m), new(2, 0m)];
        await new LabDatabase(source).ResetAsync(initial, Ct);
        var request = Create(1, 2, 100.01m);
        var result = await new TransferService(source).ExecuteAsync(request, Ct);
        Assert.Equal(TransferStatus.InsufficientFunds, result.Status);
        await AssertNoEffectsAsync(initial, request);
    }

    [Fact]
    public async Task T3_SecondWriteFailureRollsBackFirstAccountAndVersion()
    {
        SeedAccount[] initial = [new(1, 100m), new(2, 0m, false)];
        await new LabDatabase(source).ResetAsync(initial, Ct);
        bool firstWriteReached = false;
        var service = new TransferService(source, (step, _, _) =>
        {
            firstWriteReached |= step == TransferStep.AfterFirstAccountWrite;
            return ValueTask.CompletedTask;
        });
        var request = Create(1, 2, 30m);
        var result = await service.ExecuteAsync(request, Ct);
        Assert.True(firstWriteReached);
        Assert.Equal(TransferStatus.AccountUnavailable, result.Status);
        await AssertNoEffectsAsync(initial, request);
    }

    [Fact]
    public async Task ResetRestoresTheExactSeedAndClearsCommittedEffects()
    {
        var initial = LabDatabase.MakeSeed(907, 4);
        var database = new LabDatabase(source);
        var auditor = new LedgerAuditor(source);
        await database.ResetAsync(initial, Ct);
        var before = await auditor.ReadSnapshotAsync(Ct);
        var request = Create(4, 1, 2.34m);
        Assert.Equal(TransferStatus.Committed, (await new TransferService(source).ExecuteAsync(request, Ct)).Status);
        await database.ResetAsync(LabDatabase.MakeSeed(907, 4), Ct);
        var after = await auditor.ReadSnapshotAsync(Ct);
        Assert.Equal(before.Accounts.ToArray(), after.Accounts.ToArray());
        Assert.Empty(after.Transfers);
        Assert.Empty(after.Entries);
    }

    private async Task AssertNoEffectsAsync(SeedAccount[] initial, TransferRequest request)
    {
        var snapshot = await new LedgerAuditor(source).ReadSnapshotAsync(Ct);
        Assert.True(LedgerAuditor.Reconcile(initial, [request], snapshot).Reconciled);
        Assert.All(snapshot.Accounts, account => Assert.Equal(0L, account.Version));
        Assert.Empty(snapshot.Transfers);
        Assert.Empty(snapshot.Entries);
    }

    private static TransferRequest Create(long source, long destination, decimal amount) =>
        new(Guid.NewGuid(), Guid.NewGuid(), source, destination, amount, "NZD");
}
