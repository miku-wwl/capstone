using System.Data;
using System.Globalization;
using Capstone.Core;
using Npgsql;

namespace Capstone.Postgres;

public sealed class TransferService
{
    private readonly NpgsqlDataSource source;
    private readonly TransferProbe? probe;
    private readonly TransferPolicy policy;

    public TransferService(NpgsqlDataSource source, TransferProbe? probe = null, TransferPolicy? policy = null)
    {
        this.source = source;
        this.probe = probe;
        this.policy = policy ?? new();
        this.policy.Validate();
    }

    public async Task<TransferResult> ExecuteAsync(TransferRequest request, CancellationToken ct = default)
    {
        if (!request.IsValid)
            return new(TransferStatus.InvalidRequest);

        int versionConflicts = 0, lockTimeouts = 0, deadlocks = 0;
        for (int attempt = 1; ; attempt++)
        {
            TransferResult result;
            try
            {
                result = await ExecuteAttemptAsync(request, ct);
            }
            catch (PostgresException failure)
            {
                // ExecuteAttemptAsync only throws errors from before the COMMIT boundary.
                result = new(ClassifySqlFailure(failure));
            }
            catch (Exception failure) when (failure is NpgsqlException or IOException)
            {
                result = new(TransferStatus.DatabaseUnavailable);
            }

            var status = result.Status;
            if (status == TransferStatus.VersionConflict) versionConflicts++;
            if (status == TransferStatus.LockTimeout) lockTimeouts++;
            if (status == TransferStatus.Deadlock) deadlocks++;
            bool retryable = status is TransferStatus.VersionConflict or TransferStatus.LockTimeout
                or TransferStatus.Deadlock or TransferStatus.SerializationFailure;
            result = result with
            {
                TransactionAttempts = attempt,
                VersionConflicts = versionConflicts,
                LockTimeouts = lockTimeouts,
                Deadlocks = deadlocks
            };
            if (!retryable)
                return result;
            if (attempt > policy.RetryLimit)
                return result with { Status = TransferStatus.RetryExhausted, LastFailure = status };
            await Task.Delay(policy.Delay(request.AttemptId, attempt), ct);
        }
    }

    private async Task<TransferResult> ExecuteAttemptAsync(TransferRequest request, CancellationToken ct)
    {
        bool commitIssued = false;
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using (var timeout = Command(transaction, "SELECT set_config('lock_timeout', $1, true)",
                policy.LockTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms"))
            {
                await timeout.ExecuteNonQueryAsync(ct);
            }

            Dictionary<long, long>? versions = null;
            if (policy.VersionValidation)
            {
                versions = await ReadVersionsAsync(transaction, request, ct);
                if (versions.Count != 2)
                {
                    await transaction.RollbackAsync(ct);
                    return new(TransferStatus.AccountUnavailable);
                }
            }
            if (probe is not null)
                await probe(TransferStep.BeforeAccountWrites, request, ct);

            bool debitFirst = request.SourceAccount < request.DestinationAccount;
            var failure = await UpdateAccountAsync(transaction, request, debitFirst, versions, ct);
            if (failure is null)
            {
                if (probe is not null)
                    await probe(TransferStep.AfterFirstAccountWrite, request, ct);
                failure = await UpdateAccountAsync(transaction, request, !debitFirst, versions, ct);
            }

            if (failure is not null)
            {
                await transaction.RollbackAsync(ct);
                return new(failure.Value);
            }

            var transferId = Guid.NewGuid();
            await WriteLedgerAsync(transaction, request, transferId, ct);
            commitIssued = true;
            await transaction.CommitAsync(ct);
            return new(TransferStatus.Committed, transferId);
        }
        catch (Exception error) when (commitIssued &&
            error is NpgsqlException or IOException or OperationCanceledException)
        {
            // An interrupted COMMIT is not evidence that the database rolled back.
            return new(TransferStatus.UnknownCommit);
        }
    }

    private static async Task<Dictionary<long, long>> ReadVersionsAsync(
        NpgsqlTransaction tx, TransferRequest request, CancellationToken ct)
    {
        await using var select = Command(tx, "SELECT id, version FROM capstone.accounts WHERE id IN ($1, $2)",
            request.SourceAccount, request.DestinationAccount);
        await using var reader = await select.ExecuteReaderAsync(ct);
        var versions = new Dictionary<long, long>(capacity: 2);
        while (await reader.ReadAsync(ct))
            versions.Add(reader.GetInt64(0), reader.GetInt64(1));
        return versions;
    }

    private static async Task<TransferStatus?> UpdateAccountAsync(
        NpgsqlTransaction transaction, TransferRequest request, bool debit,
        Dictionary<long, long>? versions, CancellationToken ct)
    {
        long accountId = debit ? request.SourceAccount : request.DestinationAccount;
        var sql = debit
            ? """
              UPDATE capstone.accounts SET balance = balance - $2, version = version + 1
              WHERE id = $1 AND status = 'active' AND balance >= $2
              """
            : """
              UPDATE capstone.accounts SET balance = balance + $2, version = version + 1
              WHERE id = $1 AND status = 'active'
              """;
        long? expectedVersion = versions?[accountId];
        await using var update = expectedVersion is long expected
            ? Command(transaction, sql + " AND version = $3", accountId, request.Amount, expected)
            : Command(transaction, sql, accountId, request.Amount);
        int affected = await update.ExecuteNonQueryAsync(ct);
        if (affected == 1)
            return null;
        if (affected != 0)
            throw new InvalidOperationException("An account UPDATE must affect at most one row.");

        // Classify only a currently observable rejection, not the cause of the earlier zero.
        await using var check = Command(transaction, """
            SELECT status, balance, version FROM capstone.accounts WHERE id = $1 FOR NO KEY UPDATE
            """, accountId);
        await using var row = await check.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct) || row.GetString(0) != "active")
            return TransferStatus.AccountUnavailable;
        if (debit && row.GetDecimal(1) < request.Amount)
            return TransferStatus.InsufficientFunds;
        if (expectedVersion is long previous && row.GetInt64(2) != previous)
            return TransferStatus.VersionConflict;
        return TransferStatus.PredicateUnresolved;
    }

    private static TransferStatus ClassifySqlFailure(PostgresException failure) => failure.SqlState switch
    {
        PostgresErrorCodes.LockNotAvailable => TransferStatus.LockTimeout,
        PostgresErrorCodes.DeadlockDetected => TransferStatus.Deadlock,
        PostgresErrorCodes.SerializationFailure => TransferStatus.SerializationFailure,
        _ => TransferStatus.DatabaseFailure
    };

    private static NpgsqlCommand Command(NpgsqlTransaction tx, string sql, params object[] args)
    {
        var command = new NpgsqlCommand(sql, tx.Connection!, tx);
        foreach (object arg in args)
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        return command;
    }

    private static async Task WriteLedgerAsync(
        NpgsqlTransaction transaction, TransferRequest request, Guid transferId, CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(transaction.Connection!, transaction);
        var transfer = new NpgsqlBatchCommand("""
            INSERT INTO capstone.transfers
                (transfer_id, logical_request_id, attempt_id, source_account, destination_account, amount, currency)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """);
        transfer.Parameters.Add(new NpgsqlParameter { Value = transferId });
        transfer.Parameters.Add(new NpgsqlParameter { Value = request.LogicalRequestId });
        transfer.Parameters.Add(new NpgsqlParameter { Value = request.AttemptId });
        transfer.Parameters.Add(new NpgsqlParameter { Value = request.SourceAccount });
        transfer.Parameters.Add(new NpgsqlParameter { Value = request.DestinationAccount });
        transfer.Parameters.Add(new NpgsqlParameter { Value = request.Amount });
        transfer.Parameters.Add(new NpgsqlParameter { Value = request.Currency });
        batch.BatchCommands.Add(transfer);

        var ledger = new NpgsqlBatchCommand("""
            INSERT INTO capstone.ledger_entries (transfer_id, account_id, entry_type, signed_amount)
            VALUES ($1, $2, 'debit', -$4), ($1, $3, 'credit', $4)
            """);
        ledger.Parameters.Add(new NpgsqlParameter { Value = transferId });
        ledger.Parameters.Add(new NpgsqlParameter { Value = request.SourceAccount });
        ledger.Parameters.Add(new NpgsqlParameter { Value = request.DestinationAccount });
        ledger.Parameters.Add(new NpgsqlParameter { Value = request.Amount });
        batch.BatchCommands.Add(ledger);

        if (await batch.ExecuteNonQueryAsync(ct) != 3)
            throw new InvalidOperationException("The transaction must write one transfer and two ledger entries.");
    }
}
