using Capstone.Api;
using Capstone.Postgres;

await using var source = LabDatabase.FromEnvironment();
var database = new LabDatabase(source);
if (args is ["--migrate"])
{
    await database.MigrateAsync();
    Console.WriteLine("Capstone schema version 1 is ready.");
    return;
}

if (args is ["--reset", var seed, var size] &&
    int.TryParse(seed, out var seedValue) && int.TryParse(size, out var accountCount))
{
    await database.ResetAsync(LabDatabase.MakeSeed(seedValue, accountCount));
    Console.WriteLine($"Reset {accountCount} synthetic accounts (seed {seedValue}).");
    return;
}

if (args.Length != 0)
    throw new ArgumentException("Use --migrate, --reset <seed> <accounts>, or start without arguments.");

var port = int.Parse(Environment.GetEnvironmentVariable("CAPSTONE_API_PORT") ?? "5187");
if (port is < 1024 or > 65535)
    throw new ArgumentOutOfRangeException(nameof(port));
string group = Environment.GetEnvironmentVariable("CAPSTONE_GROUP") ?? "A";
bool faults = Environment.GetEnvironmentVariable("CAPSTONE_LAB_FAULTS") == "1";
await using var app = LabApi.Build(source, group, port, faults);
await app.RunAsync();
