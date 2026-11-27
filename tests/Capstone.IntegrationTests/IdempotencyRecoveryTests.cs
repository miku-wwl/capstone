using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capstone.Api;
using Capstone.Core;
using Capstone.Postgres;
using Npgsql;
using Xunit;

namespace Capstone.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class IdempotencyRecoveryTests : IAsyncLifetime
{
    private static readonly SeedAccount[] Initial = [new(1, 100m), new(2, 0m), new(3, 0m)];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly NpgsqlDataSource source = LabDatabase.FromEnvironment();

    public async ValueTask InitializeAsync()
    {
        var db = new LabDatabase(source);
        await db.MigrateAsync(Ct);
        await db.ResetAsync(Initial, Ct);
    }

    public ValueTask DisposeAsync() => source.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T4_ConcurrentSameKeyGetsOneEffect(bool versionValidation)
    {
        var claimed = NewGate();
        var release = NewGate();
        var input = NewTransfer();
        var repeated = Retry(input);
        var service = new TransferService(source, async (step, request, ct) =>
        {
            if (step == TransferStep.AfterKeyClaim && request.AttemptId == input.AttemptId)
            {
                claimed.SetResult();
                await release.Task.WaitAsync(ct);
            }
        }, new(VersionValidation: versionValidation, LockTimeoutMs: 4000, PersistentKeys: true));

        var original = service.ExecuteAsync(input, "t4", Ct);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var contender = service.ExecuteAsync(repeated, "t4", Ct);
        try { await WaitForUniqueKeyLockAsync(); }
        finally { release.TrySetResult(); }
        var receipts = await Task.WhenAll(original, contender);

        Assert.All(receipts, result => Assert.Equal(TransferStatus.Committed, result.Status));
        Assert.False(receipts[0].Replayed);
        Assert.True(receipts[1].Replayed);
        Assert.Equal(receipts[0].TransferId, receipts[1].TransferId);
        Assert.Single((await ReconcileAsync(Initial, [input, repeated])).Transfers);
        Assert.Equal(1L, await CompletedKeysAsync());
    }

    [Fact]
    public async Task T5_SameKeyDifferentPayloadHasNoSecondEffect()
    {
        var service = new TransferService(source, policy: new(PersistentKeys: true));
        var input = NewTransfer();
        var changed = NewTransfer() with { Amount = 31m };
        Assert.Equal(TransferStatus.Committed, (await service.ExecuteAsync(input, "t5", Ct)).Status);
        Assert.Equal(TransferStatus.KeyPayloadMismatch, (await service.ExecuteAsync(changed, "t5", Ct)).Status);
        Assert.Single((await ReconcileAsync(Initial, [input, changed])).Transfers);
    }

    [Fact]
    public async Task T6_WaiterTakesOverAfterOwnerRollsBackItsWrites()
    {
        var readyToAbort = NewGate();
        var release = NewGate();
        var input = NewTransfer();
        var repeated = Retry(input);
        var service = new TransferService(source, async (step, request, ct) =>
        {
            if (step == TransferStep.BeforeCommit && request.AttemptId == input.AttemptId)
            {
                readyToAbort.SetResult();
                await release.Task.WaitAsync(ct);
                throw new InjectedRollbackException();
            }
        }, new(LockTimeoutMs: 4000, PersistentKeys: true));

        var owner = service.ExecuteAsync(input, "t6", Ct);
        await readyToAbort.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var takeover = service.ExecuteAsync(repeated, "t6", Ct);
        try { await WaitForUniqueKeyLockAsync(); }
        finally { release.TrySetResult(); }
        var receipts = await Task.WhenAll(owner, takeover);

        Assert.Equal(TransferStatus.AbortedBeforeCommit, receipts[0].Status);
        Assert.Equal(TransferStatus.Committed, receipts[1].Status);
        Assert.False(receipts[1].Replayed);
        var snapshot = await ReconcileAsync(Initial, [input, repeated]);
        Assert.Equal(repeated.AttemptId, Assert.Single(snapshot.Transfers).AttemptId);
        Assert.Equal(1L, snapshot.Accounts[0].Version);
        Assert.Equal(1L, await CompletedKeysAsync());
    }

    [Theory]
    [InlineData("A", 2)]
    [InlineData("D", 1)]
    public async Task T7_ResponseLossAfterConfirmedCommitUsesARealHttpConnection(string group, int effects)
    {
        var events = new ConcurrentQueue<ApiAttempt>();
        await using var app = LabApi.Build(source, group, port: 0, enableFaults: true, observe: events.Enqueue);
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(8) };
        var input = NewTransfer();
        using var lostResponse = Message(input, "t7", drop: true);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var unexpected = await client.SendAsync(lostResponse, Ct);
        });
        var lost = Assert.Single(events);
        Assert.True(lost.ResponseDropped);
        Assert.Equal(TransferStatus.Committed, lost.Result.Status);

        var repeated = Retry(input);
        using var retry = Message(repeated, "t7");
        using var response = await client.SendAsync(retry, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = await response.Content.ReadFromJsonAsync<TransferReply>(WebJson, Ct);
        Assert.NotNull(reply);
        Assert.Equal(TransferStatus.Committed, reply.Status);
        Assert.Equal(effects == 1, reply.TransferId == lost.Result.TransferId);
        Assert.Equal(effects, (await ReconcileAsync(Initial, [input, repeated])).Transfers.Count);
    }

    [Fact]
    public async Task T11_DeterministicRejectionReplaysOnlyAfterFinancialSavepointRollback()
    {
        SeedAccount[] initial = [new(1, 100m), new(2, 0m, false)];
        await new LabDatabase(source).ResetAsync(initial, Ct);
        int firstWrites = 0;
        var service = new TransferService(source, (step, _, _) =>
        {
            if (step == TransferStep.AfterFirstAccountWrite)
                firstWrites++;
            return ValueTask.CompletedTask;
        }, new(PersistentKeys: true));
        var input = NewTransfer();
        var repeated = Retry(input);
        var original = await service.ExecuteAsync(input, "t11", Ct);
        var replay = await service.ExecuteAsync(repeated, "t11", Ct);
        Assert.Equal(TransferStatus.AccountUnavailable, original.Status);
        Assert.Equal(original.Status, replay.Status);
        Assert.True(replay.Replayed);
        Assert.Equal(1, firstWrites);
        var snapshot = await ReconcileAsync(initial, [input, repeated]);
        Assert.All(snapshot.Accounts, account => Assert.Equal(0, account.Version));
        Assert.Empty(snapshot.Transfers);
        Assert.Empty(snapshot.Entries);
        Assert.Equal(1L, await CompletedKeysAsync());
    }

    [Theory]
    [InlineData(false, true, TransferStatus.UnknownCommit, 1)]
    [InlineData(true, true, TransferStatus.Committed, 1)]
    [InlineData(true, false, TransferStatus.UnknownCommit, 0)]
    public async Task T12_InjectedCommitUncertaintyNeverRepeatsFinancialWrites(
        bool persistentKeys, bool afterCommit, TransferStatus expected, int effects)
    {
        var input = NewTransfer();
        var stage = afterCommit ? TransferStep.AfterCommit : TransferStep.BeforeCommit;
        var service = new TransferService(source, (step, _, _) =>
        {
            if (step == stage)
                throw new InjectedCommitUncertaintyException();
            return ValueTask.CompletedTask;
        }, new(PersistentKeys: persistentKeys));

        var receipt = await service.ExecuteAsync(input, "t12", Ct);
        Assert.Equal(expected, receipt.Status);
        Assert.Equal(1, receipt.TransactionAttempts);
        Assert.Equal(persistentKeys && afterCommit, receipt.RecoveredCommit);
        var snapshot = await ReconcileAsync(Initial, [input]);
        Assert.Equal(effects, snapshot.Transfers.Count);
        if (receipt.RecoveredCommit)
            Assert.Equal(Assert.Single(snapshot.Transfers).TransferId, receipt.TransferId);
    }

    [Fact]
    public async Task T13_KeyLockTimeoutReturnsBusyThenSameKeyRecovers()
    {
        var claimed = NewGate();
        var release = NewGate();
        var input = NewTransfer();
        var repeated = Retry(input);
        var service = new TransferService(source, async (step, request, ct) =>
        {
            if (step == TransferStep.AfterKeyClaim && request.AttemptId == input.AttemptId)
            {
                claimed.SetResult();
                await release.Task.WaitAsync(ct);
            }
        }, new(LockTimeoutMs: 100, PersistentKeys: true));
        var owner = service.ExecuteAsync(input, "t13", Ct);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try
        {
            var busy = await service.ExecuteAsync(repeated, "t13", Ct);
            Assert.Equal(TransferStatus.KeyBusy, busy.Status);
            Assert.Equal(1, busy.TransactionAttempts);
        }
        finally { release.TrySetResult(); }
        var original = await owner;
        var again = Retry(input);
        var replay = await service.ExecuteAsync(again, "t13", Ct);
        Assert.Equal(TransferStatus.Committed, original.Status);
        Assert.Equal(original.TransferId, replay.TransferId);
        Assert.True(replay.Replayed);
        Assert.Single((await ReconcileAsync(Initial, [input, repeated, again])).Transfers);
    }

    [Fact]
    public async Task T13_AccountLockTimeoutLeavesNoPartialFinancialState()
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var blocker = await connection.BeginTransactionAsync(Ct);
        await using var lockRow = new NpgsqlCommand(
            "SELECT id FROM capstone.accounts WHERE id = 2 FOR NO KEY UPDATE", connection, blocker);
        await lockRow.ExecuteNonQueryAsync(Ct);
        var input = NewTransfer();
        var service = new TransferService(source, policy: new(RetryLimit: 0, LockTimeoutMs: 100));
        var receipt = await service.ExecuteAsync(input, Ct);
        Assert.Equal(TransferStatus.RetryExhausted, receipt.Status);
        Assert.Equal(TransferStatus.LockTimeout, receipt.LastFailure);
        Assert.Equal(1, receipt.LockTimeouts);
        await blocker.RollbackAsync(Ct);
        var snapshot = await ReconcileAsync(Initial, [input]);
        Assert.Empty(snapshot.Transfers);
        Assert.All(snapshot.Accounts, account => Assert.Equal(0, account.Version));
    }

    private async Task WaitForUniqueKeyLockAsync()
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock' AND query LIKE 'INSERT INTO capstone.idempotency_records%')
            """, connection);
        for (int i = 0; i < 100; i++)
        {
            if (await command.ExecuteScalarAsync(Ct) is true)
                return;
            await Task.Delay(20, Ct);
        }
        Assert.Fail("The duplicate request did not reach the PostgreSQL uniqueness wait.");
    }

    private async Task<long> CompletedKeysAsync()
    {
        await using var connection = await source.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM capstone.idempotency_records WHERE status = 'completed'
            """, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<LedgerSnapshot> ReconcileAsync(SeedAccount[] initial, TransferRequest[] inputs)
    {
        var snapshot = await new LedgerAuditor(source).ReadSnapshotAsync(Ct);
        var audit = LedgerAuditor.Reconcile(initial, inputs, snapshot);
        Assert.True(audit.Reconciled, string.Join(Environment.NewLine, audit.Violations));
        return snapshot;
    }

    private static HttpRequestMessage Message(TransferRequest input, string key, bool drop = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/transfers") { Content = JsonContent.Create(input) };
        request.Headers.Add(LabApi.KeyHeader, key);
        if (drop)
            request.Headers.Add(LabApi.FaultHeader, LabApi.ResponseLoss);
        return request;
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TransferRequest NewTransfer() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1, 2, 30m, "NZD");

    private static TransferRequest Retry(TransferRequest input) => input with { AttemptId = Guid.NewGuid() };
}
