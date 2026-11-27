using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Capstone.Experiments;

internal static class EvidenceFiles
{
    private static readonly JsonSerializerOptions Compact = new(ExperimentRunner.Json) { WriteIndented = false };

    public static string HashJson<T>(T value) => Hex(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Compact)));

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = OpenRead(path);
        return Hex(await SHA256.HashDataAsync(stream, ct));
    }

    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = OpenRead(path);
        if (Path.GetExtension(path) == ".gz")
        {
            await using var compressed = new GZipStream(stream, CompressionMode.Decompress);
            return await ReadJsonAsync<T>(compressed, ct);
        }
        return await ReadJsonAsync<T>(stream, ct);
    }

    private static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct) =>
        await JsonSerializer.DeserializeAsync<T>(stream, ExperimentRunner.Json, ct)
        ?? throw new InvalidDataException("Evidence file contains no JSON value.");

    public static async Task WriteAsync<T>(string path, T value, bool replace, CancellationToken ct)
    {
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        await using (var stream = new FileStream(pending, FileMode.CreateNew))
        {
            if (Path.GetExtension(path) == ".gz")
            {
                await using var compressed = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
                await JsonSerializer.SerializeAsync(compressed, value, Compact, ct);
            }
            else
            {
                await JsonSerializer.SerializeAsync(stream, value, ExperimentRunner.Json, ct);
            }
            stream.Flush(flushToDisk: true);
        }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(pending, path, overwrite: replace);
                return;
            }
            catch (Exception error) when (replace && attempt < 25 && IsTemporaryWindowsFileLock(error))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static bool IsTemporaryWindowsFileLock(Exception error) => OperatingSystem.IsWindows() &&
        (error is IOException or UnauthorizedAccessException) && (error.HResult & 0xffff) is 5 or 32 or 33;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
