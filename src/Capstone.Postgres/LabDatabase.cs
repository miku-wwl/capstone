using System.Net;
using Npgsql;

namespace Capstone.Postgres;

public sealed record SeedAccount(long Id, decimal Balance, bool Active = true);

public sealed class LabDatabase(NpgsqlDataSource source)
{
    private const string Purpose = "mse907-synthetic-transfer-lab";

    public static NpgsqlDataSource FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("CAPSTONE_DB")
            ?? throw new InvalidOperationException("Set CAPSTONE_DB for the dedicated local lab.");
        var settings = new NpgsqlConnectionStringBuilder(value);
        var isLocal = settings.Host == "localhost" ||
            (IPAddress.TryParse(settings.Host, out var ip) && IPAddress.IsLoopback(ip));
        if (!isLocal || settings.Database != "capstone" || settings.Username != "capstone")
            throw new InvalidOperationException("The lab requires a local capstone database and role.");

        settings.ApplicationName = "mse907-capstone";
        return NpgsqlDataSource.Create(settings.ConnectionString);
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var migrationLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(907001)", connection, transaction);
        await migrationLock.ExecuteNonQueryAsync(ct);
        await using var marker = new NpgsqlCommand(
            "SELECT to_regclass('capstone.schema_version') IS NOT NULL", connection, transaction);
        if ((bool)(await marker.ExecuteScalarAsync(ct))!)
        {
            await EnsureLabSchemaAsync(connection, transaction, ct);
        }
        else
        {
            await using var resource = typeof(LabDatabase).Assembly.GetManifestResourceStream(
                "Capstone.Postgres.Migrations.001_initial.sql")
                ?? throw new InvalidOperationException("Embedded schema migration is missing.");
            using var reader = new StreamReader(resource);
            await using var migration = new NpgsqlCommand(
                await reader.ReadToEndAsync(ct), connection, transaction);
            await migration.ExecuteNonQueryAsync(ct);
            await using var version = new NpgsqlCommand(
                "INSERT INTO capstone.schema_version (version, purpose) VALUES (1, $1)",
                connection, transaction);
            version.Parameters.Add(new NpgsqlParameter { Value = Purpose });
            await version.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public static IReadOnlyList<SeedAccount> MakeSeed(int seed, int accounts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(accounts, 2);
        var rng = new Random(seed);
        return Enumerable.Range(1, accounts)
            .Select(id => new SeedAccount(id, 10_000m + rng.Next(0, 100_000) / 100m))
            .ToArray();
    }

    public async Task ResetAsync(IReadOnlyList<SeedAccount> seed, CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await EnsureLabSchemaAsync(connection, transaction, ct);
        await using var reset = new NpgsqlBatch(connection, transaction);
        reset.BatchCommands.Add(new NpgsqlBatchCommand("""
            TRUNCATE TABLE capstone.idempotency_records, capstone.ledger_entries,
                capstone.transfers, capstone.accounts
            """));
        var insert = new NpgsqlBatchCommand("""
            INSERT INTO capstone.accounts (id, balance, version, status)
            SELECT id, balance, 0, status FROM unnest($1::bigint[], $2::numeric[], $3::text[])
                AS initial(id, balance, status)
            """);
        insert.Parameters.Add(new NpgsqlParameter { Value = seed.Select(x => x.Id).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter { Value = seed.Select(x => x.Balance).ToArray() });
        insert.Parameters.Add(new NpgsqlParameter
        {
            Value = seed.Select(x => x.Active ? "active" : "inactive").ToArray()
        });
        reset.BatchCommands.Add(insert);
        await reset.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task EnsureLabSchemaAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (connection.Database != "capstone")
            throw new InvalidOperationException("Refusing to use a database outside the lab.");
        await using var command = new NpgsqlCommand(
            "SELECT purpose FROM capstone.schema_version WHERE version = 1", connection, transaction);
        if (await command.ExecuteScalarAsync(ct) is not string purpose || purpose != Purpose)
            throw new InvalidOperationException("Dedicated lab schema version 1 is required.");
    }
}
