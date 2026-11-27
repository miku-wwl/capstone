using System.Data;
using Capstone.Core;
using Npgsql;

namespace Capstone.Postgres;

public sealed record AccountSnapshot(long Id, decimal Balance, long Version, bool Active);
public sealed record TransferSnapshot(
    Guid TransferId, Guid LogicalRequestId, Guid AttemptId,
    long SourceAccount, long DestinationAccount, decimal Amount, string Currency);
public sealed record EntrySnapshot(Guid TransferId, long AccountId, string EntryType, decimal SignedAmount);
public sealed record LedgerSnapshot(
    IReadOnlyList<AccountSnapshot> Accounts,
    IReadOnlyList<TransferSnapshot> Transfers,
    IReadOnlyList<EntrySnapshot> Entries);
public sealed record AuditReport(
    IReadOnlyList<string> Violations,
    IReadOnlyDictionary<Guid, int> EffectsByLogicalRequest)
{
    public bool Reconciled => Violations.Count == 0;
}

public sealed class LedgerAuditor(NpgsqlDataSource source)
{
    public async Task<LedgerSnapshot> ReadSnapshotAsync(CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var accounts = await ReadAsync(tx,
            "SELECT id, balance, version, status FROM capstone.accounts ORDER BY id",
            r => new AccountSnapshot(r.GetInt64(0), r.GetDecimal(1), r.GetInt64(2), r.GetString(3) == "active"), ct);
        var transfers = await ReadAsync(tx, """
            SELECT transfer_id, logical_request_id, attempt_id, source_account, destination_account, amount, currency
            FROM capstone.transfers ORDER BY transfer_id
            """, r => new TransferSnapshot(
                r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt64(3), r.GetInt64(4),
                r.GetDecimal(5), r.GetString(6)), ct);
        var entries = await ReadAsync(tx, """
            SELECT transfer_id, account_id, entry_type, signed_amount
            FROM capstone.ledger_entries ORDER BY transfer_id, entry_type
            """, r => new EntrySnapshot(r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetDecimal(3)), ct);
        await tx.CommitAsync(ct);
        return new(accounts, transfers, entries);
    }

    public static AuditReport Reconcile(
        IReadOnlyList<SeedAccount> initial, IReadOnlyList<TransferRequest> inputs,
        LedgerSnapshot snapshot)
    {
        var violations = new List<string>();
        var expectedBalances = initial.ToDictionary(x => x.Id, x => x.Balance);
        var inputsByAttempt = inputs.ToDictionary(x => x.AttemptId);
        var entries = snapshot.Entries.ToLookup(x => x.TransferId);
        var transferIds = snapshot.Transfers.Select(x => x.TransferId).ToHashSet();
        foreach (var transfer in snapshot.Transfers)
        {
            if (!inputsByAttempt.TryGetValue(transfer.AttemptId, out var input) ||
                input.LogicalRequestId != transfer.LogicalRequestId ||
                input.SourceAccount != transfer.SourceAccount ||
                input.DestinationAccount != transfer.DestinationAccount ||
                input.Amount != transfer.Amount || input.Currency != transfer.Currency)
                violations.Add($"Transfer {transfer.TransferId} does not match its input attempt.");

            var pair = entries[transfer.TransferId].ToArray();
            if (pair.Length != 2 ||
                !pair.Any(x => x.EntryType == "debit" && x.AccountId == transfer.SourceAccount && x.SignedAmount == -transfer.Amount) ||
                !pair.Any(x => x.EntryType == "credit" && x.AccountId == transfer.DestinationAccount && x.SignedAmount == transfer.Amount))
                violations.Add($"Transfer {transfer.TransferId} has an incorrect ledger pair.");
        }

        foreach (var entry in snapshot.Entries)
        {
            if (!transferIds.Contains(entry.TransferId))
                violations.Add($"Ledger entry without a transfer {entry.TransferId}.");
            if (!expectedBalances.TryGetValue(entry.AccountId, out decimal start))
                violations.Add($"Unexpected ledger account {entry.AccountId}.");
            else
                expectedBalances[entry.AccountId] = start + entry.SignedAmount;
        }

        foreach (var account in snapshot.Accounts)
        {
            if (account.Balance < 0 || !expectedBalances.Remove(account.Id, out var expected) || expected != account.Balance)
                violations.Add($"Account {account.Id} failed balance reconciliation.");
        }

        if (expectedBalances.Count > 0)
            violations.Add("One or more initially seeded accounts are missing.");
        if (initial.Sum(x => x.Balance) != snapshot.Accounts.Sum(x => x.Balance))
            violations.Add("Total balance is not conserved.");

        var effects = snapshot.Transfers.GroupBy(x => x.LogicalRequestId)
            .ToDictionary(grp => grp.Key, grp => grp.Count());
        return new(violations, effects);
    }

    private static async Task<List<T>> ReadAsync<T>(
        NpgsqlTransaction tx, string sql, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        await using var query = new NpgsqlCommand(sql, tx.Connection!, tx);
        await using var reader = await query.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }
}
