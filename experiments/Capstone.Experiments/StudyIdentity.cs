using System.Diagnostics;
using System.Runtime.InteropServices;
using Npgsql;

namespace Capstone.Experiments;

internal sealed record StudyScope(string Status, string ConfirmedOn, string Source, string Scope,
    bool HumanParticipants, bool RealFinancialOrPersonalData, bool InstitutionalReferenceProvided)
{
    public static async Task<string> ReadHashAsync(string path, CancellationToken ct)
    {
        var record = await EvidenceFiles.ReadAsync<StudyScope>(path, ct);
        if (record.Status != "student-confirmed-no-ethics-approval-required" ||
            record.HumanParticipants || record.RealFinancialOrPersonalData ||
            !DateOnly.TryParse(record.ConfirmedOn, out _) || string.IsNullOrWhiteSpace(record.Source) ||
            string.IsNullOrWhiteSpace(record.Scope))
            throw new InvalidDataException("The recorded student confirmation must cover this synthetic study.");
        return await EvidenceFiles.HashAsync(path, ct);
    }
}

internal sealed record FileFingerprint(string File, string Sha256);
internal sealed record RuntimeIdentity(string DotNet, string Os, string Architecture, int LogicalCpu,
    string PostgreSqlConfiguration, string ContainerImageAndLimits, string DockerVmResources, int PoolLimit);
internal sealed record StudyIdentity(string ObservedHead, string SourceSha256, string BuildSha256,
    FileFingerprint[] SourceFiles, FileFingerprint[] BuildFiles, RuntimeIdentity Runtime)
{
    private static readonly string[] Inputs =
        ["src", "experiments/Capstone.Experiments", "experiments/workloads", "infrastructure",
         "global.json", "Directory.Build.props", "Capstone.slnx"];

    public static async Task<StudyIdentity> CaptureAsync(NpgsqlConnection database, int poolLimit, CancellationToken ct)
    {
        string head = await CommandAsync("git", ["rev-parse", "HEAD"], ct);
        var inputs = await SourceAsync(ct);
        var binaries = await BinariesAsync(ct);
        string image = await CommandAsync("docker", ["inspect", "mse907-capstone-postgres-1", "--format",
            "{{.Config.Image}}|{{.Image}}|{{.HostConfig.NanoCpus}}|{{.HostConfig.Memory}}"], ct);
        string limits = await CommandAsync("docker", ["info", "--format", "{{.NCPU}}|{{.MemTotal}}"], ct);
        await using var query = new NpgsqlCommand("""
            SELECT concat_ws('|', current_setting('server_version'), current_setting('synchronous_commit'),
                current_setting('fsync'), current_setting('full_page_writes'), current_setting('shared_buffers'),
                current_setting('max_connections'))
            """, database);
        var pg = (string)(await query.ExecuteScalarAsync(ct))!;
        return new(head, EvidenceFiles.HashJson(inputs), EvidenceFiles.HashJson(binaries), inputs, binaries,
            new(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, pg, image, limits, poolLimit));
    }

    public bool SameExecutionAs(StudyIdentity other) =>
        SourceSha256 == other.SourceSha256 && BuildSha256 == other.BuildSha256 && Runtime == other.Runtime;

    public async Task VerifyFilesAsync(CancellationToken ct)
    {
        if (EvidenceFiles.HashJson(await SourceAsync(ct)) != SourceSha256 ||
            EvidenceFiles.HashJson(await BinariesAsync(ct)) != BuildSha256)
            throw new InvalidDataException("Experiment source or binaries changed during the study.");
    }

    public static async Task VerifyFormalCommitAsync(string codeCommit, string protocolPath, CancellationToken ct)
    {
        if (codeCommit.Length != 40 || !codeCommit.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid frozen Git commit.");
        var changes = await CommandAsync("git", ["status", "--porcelain", "--untracked-files=all", "--", .. Inputs, protocolPath], ct);
        if (changes.Length != 0)
            throw new InvalidDataException("Formal execution requires a committed protocol and unchanged experiment sources.");
        string treeChanges = await CommandAsync("git", ["diff", "--name-only", codeCommit, "HEAD", "--", .. Inputs], ct);
        if (treeChanges.Length != 0)
            throw new InvalidDataException("Current experiment sources differ from the frozen Git commit.");
        await CommandAsync("git", ["ls-files", "--error-unmatch", "--", protocolPath], ct);
    }

    public static async Task<string> DatabaseEpochAsync(NpgsqlConnection database, CancellationToken ct)
    {
        await using var query = new NpgsqlCommand("SELECT pg_postmaster_start_time()::text", database);
        return (string)(await query.ExecuteScalarAsync(ct))!;
    }

    private static async Task<FileFingerprint[]> SourceAsync(CancellationToken ct)
    {
        var listed = await CommandAsync("git",
            ["-c", "core.quotepath=false", "ls-files", "-co", "--exclude-standard", "-z", "--", .. Inputs], ct);
        var files = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return await Task.WhenAll(files.Select(async f => new FileFingerprint(f, await EvidenceFiles.HashAsync(f, ct))));
    }

    private static async Task<FileFingerprint[]> BinariesAsync(CancellationToken ct)
    {
        var files = Directory.GetFiles(AppContext.BaseDirectory)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        return await Task.WhenAll(files.Select(async f => new FileFingerprint(Path.GetFileName(f), await EvidenceFiles.HashAsync(f, ct))));
    }

    private static async Task<string> CommandAsync(string file, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {file}.");
        var read = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await errors;
        var output = await read;
        if (process.ExitCode != 0) throw new IOException($"Required {file} environment check failed.");
        return output.Trim();
    }
}
