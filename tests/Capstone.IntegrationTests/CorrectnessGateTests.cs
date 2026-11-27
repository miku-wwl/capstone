using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using Capstone.Api;
using Capstone.Core;
using Capstone.Postgres;
using Npgsql;
using Xunit;

namespace Capstone.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class CorrectnessGateTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly NpgsqlDataSource source = LabDatabase.FromEnvironment();

    public async ValueTask InitializeAsync() => await new LabDatabase(source).MigrateAsync(Ct);

    public ValueTask DisposeAsync() => source.DisposeAsync();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CommonFinancialConstraintsHoldInEveryGroup(bool versionValidation, bool keys)
    {
        SeedAccount[] initial = [new(1, 100m), new(2, 0m), new(3, 0m, false)];
        await new LabDatabase(source).ResetAsync(initial, Ct);
        var service = new TransferService(source,
            policy: new(VersionValidation: versionValidation, PersistentKeys: keys));
        var planned = Request(2, 30m);
        var overdraft = Request(2, 71m);
        var secondUnavailable = Request(3, 1m);
        Assert.Equal(TransferStatus.Committed, (await Send(planned)).Status);
        Assert.Equal(TransferStatus.InsufficientFunds, (await Send(overdraft)).Status);
        Assert.Equal(TransferStatus.AccountUnavailable, (await Send(secondUnavailable)).Status);
        var snapshot = await new LedgerAuditor(source).ReadSnapshotAsync(Ct);
        var audit = LedgerAuditor.Reconcile(initial, [planned, overdraft, secondUnavailable], snapshot);
        Assert.True(audit.Reconciled, string.Join(Environment.NewLine, audit.Violations));
        Assert.Single(snapshot.Transfers);
        Assert.Equal(70m, snapshot.Accounts[0].Balance);
        Assert.Equal(1L, snapshot.Accounts[0].Version);

        Task<TransferResult> Send(TransferRequest request) =>
            service.ExecuteAsync(request, request.LogicalRequestId.ToString("N"), Ct);
    }

    [Fact]
    public async Task W3_RejectsBeforeAnyTransferTransaction()
    {
        SeedAccount[] initial = [new(1, 100m), new(2, 0m)];
        await new LabDatabase(source).ResetAsync(initial, Ct);
        var events = new ConcurrentQueue<ApiAttempt>();
        await using var app = LabApi.Build(source, "D", port: 0, enableFaults: true, observe: events.Enqueue);
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var input = Request(2, 30m);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/transfers") { Content = JsonContent.Create(input) };
        post.Headers.Add(LabApi.KeyHeader, "w3");
        post.Headers.Add(LabApi.FaultHeader, LabApi.BeforeTransaction);
        using var response = await client.SendAsync(post, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var trace = Assert.Single(events);
        Assert.Equal(TransferStatus.DeniedBeforeTransaction, trace.Result.Status);
        Assert.Equal(0, trace.Result.TransactionAttempts);
        var snapshot = await new LedgerAuditor(source).ReadSnapshotAsync(Ct);
        Assert.True(LedgerAuditor.Reconcile(initial, [input], snapshot).Reconciled);
        Assert.Empty(snapshot.Transfers);
        await using var keyCount = source.CreateCommand("SELECT count(*) FROM capstone.idempotency_records");
        await using var check = await keyCount.ExecuteReaderAsync(Ct);
        Assert.True(await check.ReadAsync(Ct));
        Assert.Equal(0L, check.GetInt64(0));
    }

    [Fact]
    public async Task T10_W5DemonstratesStaleOverwriteOnlyInAnIsolatedDiagnosticTable()
    {
        string table = "capstone.stale_diagnostic_" + Guid.NewGuid().ToString("N");
        await using var control = await source.OpenConnectionAsync(Ct);
        await ExecuteAsync(control, null, $"CREATE TABLE {table} (balance numeric(19,2) NOT NULL)");
        try
        {
            await ExecuteAsync(control, null, $"INSERT INTO {table} VALUES (100)");
            await using var first = await source.OpenConnectionAsync(Ct);
            await using var second = await source.OpenConnectionAsync(Ct);
            await using var tx1 = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
            await using var tx2 = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, Ct);
            var old1 = await BalanceAsync(first, tx1);
            var old2 = await BalanceAsync(second, tx2);
            Assert.Equal(100m, old1);
            Assert.Equal(old1, old2);
            await ExecuteAsync(first, tx1, $"UPDATE {table} SET balance = $1", old1 - 10m);
            await tx1.CommitAsync(Ct);
            await ExecuteAsync(second, tx2, $"UPDATE {table} SET balance = $1", old2 - 15m);
            await tx2.CommitAsync(Ct);
            var observed = await BalanceAsync(control, null);
            Assert.Equal(85m, observed);
            Assert.NotEqual(75m, observed);
        }
        finally
        {
            await ExecuteAsync(control, null, $"DROP TABLE {table}");
        }

        async Task<decimal> BalanceAsync(NpgsqlConnection connection, NpgsqlTransaction? tx)
        {
            await using var command = new NpgsqlCommand($"SELECT balance FROM {table}", connection, tx);
            return (decimal)(await command.ExecuteScalarAsync(Ct))!;
        }
    }

    [Fact]
    public async Task ClosingAdmissionWaitsForWorkAndRejectsOldPhaseOnReopen()
    {
        var admission = new LabAdmission();
        string old = admission.Open();
        Assert.True(admission.TryEnter(old));
        var closing = admission.CloseAsync(Ct);
        Assert.False(closing.IsCompleted);
        Assert.False(admission.TryEnter(old));
        admission.Exit();
        await closing;
        string next = admission.Open();
        Assert.False(admission.TryEnter(old));
        Assert.True(admission.TryEnter(next));
        admission.Exit();
        await admission.CloseAsync(Ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? tx,
        string sql, decimal? balance = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        if (balance is decimal amount)
            command.Parameters.Add(new NpgsqlParameter { Value = amount });
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static TransferRequest Request(long destination, decimal amount) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, destination, amount, "NZD");
}
