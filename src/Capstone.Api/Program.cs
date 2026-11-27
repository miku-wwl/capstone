using System.Text.Json;
using System.Text.Json.Serialization;
using Capstone.Core;
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
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
var app = builder.Build();
var transfers = new TransferService(source);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", group = "A" }));
app.MapPost("/transfers", async (TransferRequest request, CancellationToken ct) =>
{
    var result = await transfers.ExecuteAsync(request, ct);
    int statusCode = result.Status switch
    {
        TransferStatus.Committed => StatusCodes.Status200OK,
        TransferStatus.InvalidRequest => StatusCodes.Status400BadRequest,
        TransferStatus.AccountUnavailable => StatusCodes.Status404NotFound,
        TransferStatus.InsufficientFunds => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status503ServiceUnavailable
    };
    return Results.Json(result, statusCode: statusCode);
});

await app.RunAsync();
