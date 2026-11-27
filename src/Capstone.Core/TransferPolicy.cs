using System.Buffers.Binary;

namespace Capstone.Core;

public sealed record TransferPolicy(
    bool VersionValidation = false,
    int RetryLimit = 3,
    int LockTimeoutMs = 1500,
    int InitialBackoffMs = 5,
    int BackoffCapMs = 50,
    int JitterSeed = 907,
    bool PersistentKeys = false)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryLimit, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RetryLimit, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(LockTimeoutMs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LockTimeoutMs, 5000);
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialBackoffMs, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(BackoffCapMs, InitialBackoffMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BackoffCapMs, 500);
    }

    public TimeSpan Delay(Guid attemptId, int completedAttempts)
    {
        int exponential = Math.Min(BackoffCapMs, InitialBackoffMs << (completedAttempts - 1));
        if (exponential == 0)
            return TimeSpan.Zero;

        Span<byte> bytes = stackalloc byte[16];
        attemptId.TryWriteBytes(bytes);
        uint mixed = BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            ^ unchecked((uint)JitterSeed + (uint)completedAttempts * 0x9e3779b9u);
        mixed ^= mixed >> 16;
        int jitter = (int)(mixed % (uint)(exponential + 1));
        return TimeSpan.FromMilliseconds(Math.Min(BackoffCapMs, exponential + jitter));
    }
}
