using System.Security.Cryptography;
using System.Text;
using Capstone.Core;
using Npgsql;
using NpgsqlTypes;

namespace Capstone.Postgres;

internal sealed record DurableKey(string Value, byte[] Fingerprint)
{
    public static DurableKey? Create(string? key, TransferRequest request)
    {
        if (key is null || key.Length is < 1 or > 128 ||
            key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.')))
            return null;

        string payload = FormattableString.Invariant(
            $"{request.SourceAccount}|{request.DestinationAccount}|{request.Amount * 100:0}|{request.Currency}");
        return new(key, SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

internal static class DurableKeys
{
    // The lab has one synthetic tenant and one transfer endpoint.
    public static async Task<bool> ClaimAsync(NpgsqlTransaction tx, DurableKey key, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO capstone.idempotency_records(scope, endpoint, key, fingerprint, status)
            VALUES ('synthetic-tenant', '/transfers', $1, $2, 'processing')
            ON CONFLICT (scope, endpoint, key) DO NOTHING
            """, tx.Connection!, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = key.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = key.Fingerprint });
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public static async Task<TransferResult?> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, DurableKey key, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT fingerprint, status, (response->>'status')::integer, transfer_id
            FROM capstone.idempotency_records
            WHERE scope = 'synthetic-tenant' AND endpoint = '/transfers' AND key = $1
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = key.Value });
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct))
            return null;
        if (!CryptographicOperations.FixedTimeEquals(row.GetFieldValue<byte[]>(0), key.Fingerprint))
            return new(TransferStatus.KeyPayloadMismatch);
        if (row.GetString(1) != "completed" || row.IsDBNull(2))
            return null;

        Guid? transferId = row.IsDBNull(3) ? null : row.GetGuid(3);
        return new((TransferStatus)row.GetInt32(2), transferId, Replayed: true);
    }

    public static async Task CompleteAsync(
        NpgsqlTransaction tx, DurableKey key, TransferResult result, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE capstone.idempotency_records SET status = 'completed',
                response = jsonb_build_object('status', $3::integer, 'transferId', $4::uuid),
                transfer_id = $4, completed_at = clock_timestamp()
            WHERE scope = 'synthetic-tenant' AND endpoint = '/transfers'
                AND key = $1 AND fingerprint = $2 AND status = 'processing'
            """, tx.Connection!, tx);
        command.Parameters.Add(new NpgsqlParameter { Value = key.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = key.Fingerprint });
        command.Parameters.Add(new NpgsqlParameter { Value = (int)result.Status });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = result.TransferId is Guid id ? id : DBNull.Value
        });
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("The transaction must complete its claimed request key.");
    }
}
