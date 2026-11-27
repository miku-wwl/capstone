using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capstone.Core;
using Capstone.Postgres;
using Npgsql;

namespace Capstone.Api;

public sealed record TransferReply(TransferStatus Status, Guid? TransferId);

public sealed record ApiAttempt(TransferRequest Request, TransferResult Result, bool ResponseDropped, long ReceivedTick = 0);

public static class LabApi
{
    public const string KeyHeader = "Idempotency-Key";
    public const string FaultHeader = "X-Capstone-Lab-Fault";
    public const string ResponseLoss = "drop-after-commit";
    public const string BeforeTransaction = "before-transaction";

    public static WebApplication Build(
        NpgsqlDataSource source, string group, int port, bool enableFaults = false,
        TransferPolicy? settings = null, Action<ApiAttempt>? observe = null, LabAdmission? admission = null)
    {
        if (group is not ("A" or "B" or "C" or "D"))
            throw new ArgumentException("CAPSTONE_GROUP must be A, B, C, or D.", nameof(group));
        if (port != 0 && port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LabApi).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        var app = builder.Build();
        var policy = (settings ?? new()) with
        {
            VersionValidation = group is "B" or "D",
            PersistentKeys = group is "C" or "D"
        };
        var service = new TransferService(source, policy: policy);

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", group }));
        app.MapPost("/transfers", async (HttpContext http, TransferRequest request, CancellationToken ct) =>
        {
            if (admission is not null &&
                !admission.TryEnter(http.Request.Headers[LabAdmission.Header].ToString()))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            try
            {
                long received = observe is null ? 0 : Stopwatch.GetTimestamp();
                var keys = http.Request.Headers[KeyHeader];
                string? key = keys.Count == 1 ? keys[0] : null;
                var result = enableFaults && http.Request.Headers[FaultHeader] == BeforeTransaction
                    ? new TransferResult(TransferStatus.DeniedBeforeTransaction)
                    : await service.ExecuteAsync(request, key, ct);
                bool drop = enableFaults && result.Status == TransferStatus.Committed &&
                    !result.Replayed && http.Request.Headers[FaultHeader] == ResponseLoss;
                observe?.Invoke(new(request, result, drop, received));
                if (drop)
                {
                    http.Abort();
                    return Results.Empty;
                }

                http.Response.Headers["X-Capstone-Transaction-Attempts"] =
                    result.TransactionAttempts.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["X-Capstone-Version-Conflicts"] =
                    result.VersionConflicts.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["X-Capstone-Replayed"] = result.Replayed ? "1" : "0";
                return Results.Json(new TransferReply(result.Status, result.TransferId),
                    statusCode: StatusCodeFor(result.Status));
            }
            finally
            {
                admission?.Exit();
            }
        });

        return app;
    }

    private static int StatusCodeFor(TransferStatus status) => status switch
    {
        TransferStatus.Committed => StatusCodes.Status200OK,
        TransferStatus.InvalidRequest or TransferStatus.InvalidKey => StatusCodes.Status400BadRequest,
        TransferStatus.AccountUnavailable => StatusCodes.Status404NotFound,
        TransferStatus.InsufficientFunds or TransferStatus.KeyPayloadMismatch => StatusCodes.Status422UnprocessableEntity,
        TransferStatus.KeyBusy => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status503ServiceUnavailable
    };
}
