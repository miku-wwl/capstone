using System.Data;
using Capstone.Core;
using Npgsql;

namespace Capstone.Postgres;

public sealed class TransferService(NpgsqlDataSource source, TransferProbe? probe = null)
{
    public async Task<TransferResult> ExecuteAsync(TransferRequest request, CancellationToken ct = default)
    {
        if (!request.IsValid)
            return new(TransferStatus.InvalidRequest);

        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (probe is not null)
            await probe(TransferStep.BeforeAccountWrites, request, ct);

        bool debitFirst = request.SourceAccount < request.DestinationAccount;
        var failure = await UpdateAccountAsync(transaction, request, debitFirst, ct);
        if (failure is null)
        {
            if (probe is not null)
                await probe(TransferStep.AfterFirstAccountWrite, request, ct);
            failure = await UpdateAccountAsync(transaction, request, !debitFirst, ct);
        }

        if (failure is not null)
        {
            await transaction.RollbackAsync(ct);
            return new(failure.Value);
        }

        var transferId = Guid.NewGuid();
        await WriteLedgerAsync(transaction, request, transferId, ct);

        try
        {
            await transaction.CommitAsync(ct);
            return new(TransferStatus.Committed, transferId);
        }
        catch (Exception error) when (error is NpgsqlException or IOException or OperationCanceledException)
        {
            // An interrupted COMMIT is not evidence that the database rolled back.
            return new(TransferStatus.UnknownCommit);
        }
    }

    private static async Task<TransferStatus?> UpdateAccountAsync(
        NpgsqlTransaction transaction, TransferRequest request, bool debit, CancellationToken ct)
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
        await using var update = new NpgsqlCommand(sql, transaction.Connection!, transaction);
        update.Parameters.Add(new NpgsqlParameter { Value = accountId });
        update.Parameters.Add(new NpgsqlParameter { Value = request.Amount });
        int affected = await update.ExecuteNonQueryAsync(ct);
        if (affected == 1)
            return null;
        if (affected != 0)
            throw new InvalidOperationException("An account UPDATE must affect at most one row.");

        // Classify only a currently observable rejection, not the cause of the earlier zero.
        await using var check = new NpgsqlCommand("""
            SELECT status, balance FROM capstone.accounts WHERE id = $1 FOR NO KEY UPDATE
            """, transaction.Connection!, transaction);
        check.Parameters.Add(new NpgsqlParameter { Value = accountId });
        await using var row = await check.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct) || row.GetString(0) != "active")
            return TransferStatus.AccountUnavailable;
        if (debit && row.GetDecimal(1) < request.Amount)
            return TransferStatus.InsufficientFunds;
        return TransferStatus.PredicateUnresolved;
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
