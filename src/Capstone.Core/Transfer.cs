namespace Capstone.Core;

public sealed record TransferRequest(
    Guid LogicalRequestId,
    Guid AttemptId,
    long SourceAccount,
    long DestinationAccount,
    decimal Amount,
    string Currency)
{
    public bool IsValid =>
        LogicalRequestId != Guid.Empty &&
        AttemptId != Guid.Empty &&
        SourceAccount > 0 &&
        DestinationAccount > 0 &&
        SourceAccount != DestinationAccount &&
        Amount > 0 &&
        Amount <= 99_999_999_999_999_999.99m &&
        Amount == decimal.Round(Amount, 2) &&
        Currency == "NZD";
}

public enum TransferStatus
{
    Committed,
    InvalidRequest,
    AccountUnavailable,
    InsufficientFunds,
    PredicateUnresolved,
    UnknownCommit,
    VersionConflict,
    LockTimeout,
    Deadlock,
    SerializationFailure,
    DatabaseUnavailable,
    DatabaseFailure,
    RetryExhausted
}

public sealed record TransferResult(
    TransferStatus Status,
    Guid? TransferId = null,
    int TransactionAttempts = 0,
    int VersionConflicts = 0,
    int LockTimeouts = 0,
    int Deadlocks = 0,
    TransferStatus? LastFailure = null);

public enum TransferStep
{
    BeforeAccountWrites,
    AfterFirstAccountWrite
}

public delegate ValueTask TransferProbe(
    TransferStep step, TransferRequest request, CancellationToken cancellationToken);
