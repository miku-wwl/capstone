using Capstone.Core;
using Capstone.Postgres;
using Npgsql;
using Xunit;

namespace Capstone.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class VersionValidationTests : IAsyncLifetime
{
    private readonly NpgsqlDataSource source = LabDatabase.FromEnvironment();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly SeedAccount[] Initial = [new(1, 100m), new(2, 100m), new(3, 0m)];

    public async ValueTask InitializeAsync()
    {
        var db = new LabDatabase(source);
        await db.MigrateAsync(Ct);
        await db.ResetAsync(Initial, Ct);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();

    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 2, 1)]
    public async Task T8_SecondAccountVersionChangeRetriesTheWholeTransaction(
        bool versionValidation, int expectedAttempts, int expectedConflicts)
    {
        var primary = NewTransfer(1, 2, 30m);
        var competing = NewTransfer(2, 3, 1m);
        var baseline = new TransferService(source);
        int activity = 0;
        int firstWrites = 0;
        var subject = new TransferService(source, async (step, _, token) =>
        {
            if (step == TransferStep.BeforeAccountWrites && ++activity == 1)
                Assert.Equal(TransferStatus.Committed, (await baseline.ExecuteAsync(competing, token)).Status);
            if (step == TransferStep.AfterFirstAccountWrite)
                firstWrites++;
        }, new(VersionValidation: versionValidation, InitialBackoffMs: 0));

        var receipt = await subject.ExecuteAsync(primary, Ct);
        Assert.Equal(TransferStatus.Committed, receipt.Status);
        Assert.Equal(expectedAttempts, receipt.TransactionAttempts);
        Assert.Equal(expectedConflicts, receipt.VersionConflicts);
        Assert.Equal(expectedAttempts, firstWrites);
        var snapshot = await new LedgerAuditor(source).ReadSnapshotAsync(Ct);
        Assert.True(LedgerAuditor.Reconcile(Initial, [primary, competing], snapshot).Reconciled);
        Assert.Equal(70m, snapshot.Accounts[0].Balance);
        Assert.Equal(1L, snapshot.Accounts[0].Version);
        Assert.Equal(129m, snapshot.Accounts[1].Balance);
        Assert.Equal(2, snapshot.Transfers.Count);
    }

    [Fact]
    public async Task T9_FourConflictsExhaustRetriesWithoutBusinessEffects()
    {
        var primary = NewTransfer(1, 2, 30m);
        var independent = new List<TransferRequest>();
        var baseline = new TransferService(source);
        var subject = new TransferService(source, async (step, _, token) =>
        {
            if (step != TransferStep.BeforeAccountWrites)
                return;
            var competing = NewTransfer(2, 3, 1m);
            independent.Add(competing);
            Assert.Equal(TransferStatus.Committed, (await baseline.ExecuteAsync(competing, token)).Status);
        }, new(VersionValidation: true, InitialBackoffMs: 0));

        var receipt = await subject.ExecuteAsync(primary, Ct);
        Assert.Equal(TransferStatus.RetryExhausted, receipt.Status);
        Assert.Equal(TransferStatus.VersionConflict, receipt.LastFailure);
        Assert.Equal(4, receipt.TransactionAttempts);
        Assert.Equal(4, receipt.VersionConflicts);
        Assert.Null(receipt.TransferId);

        var snapshot = await new LedgerAuditor(source).ReadSnapshotAsync(Ct);
        var audit = LedgerAuditor.Reconcile(Initial, [primary, .. independent], snapshot);
        Assert.True(audit.Reconciled, string.Join(Environment.NewLine, audit.Violations));
        Assert.False(audit.EffectsByLogicalRequest.ContainsKey(primary.LogicalRequestId));
        Assert.Equal(100m, snapshot.Accounts[0].Balance);
        Assert.Equal(0L, snapshot.Accounts[0].Version);
        Assert.Equal(4, snapshot.Transfers.Count);
    }

    [Fact]
    public async Task InsufficientFundsIsNotRetriedAsAConflict()
    {
        var receipt = await new TransferService(source, policy: new(VersionValidation: true))
            .ExecuteAsync(NewTransfer(1, 2, 100.01m), Ct);
        Assert.Equal(TransferStatus.InsufficientFunds, receipt.Status);
        Assert.Equal(1, receipt.TransactionAttempts);
        Assert.Equal(0, receipt.VersionConflicts);
    }

    private static TransferRequest NewTransfer(long sender, long recipient, decimal amount) =>
        new(Guid.NewGuid(), Guid.NewGuid(), sender, recipient, amount, "NZD");
}
