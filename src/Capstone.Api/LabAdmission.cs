namespace Capstone.Api;

public sealed class LabAdmission
{
    public const string Header = "X-Capstone-Lab-Phase";
    private readonly object sync = new();
    private string? acceptedPhase;
    private int active;
    private TaskCompletionSource? idle;

    public string Open()
    {
        lock (sync)
        {
            if (acceptedPhase is not null || active != 0)
                throw new InvalidOperationException("The previous lab phase has not ended.");
            acceptedPhase = Guid.NewGuid().ToString("N");
            return acceptedPhase;
        }
    }

    public bool TryEnter(string phase)
    {
        lock (sync)
        {
            if (acceptedPhase is null || !string.Equals(phase, acceptedPhase, StringComparison.Ordinal))
                return false;
            if (active++ == 0)
                idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public void Exit()
    {
        lock (sync)
        {
            if (--active == 0)
                idle?.SetResult();
        }
    }

    public Task CloseAsync(CancellationToken ct)
    {
        lock (sync)
        {
            acceptedPhase = null;
            return idle?.Task.WaitAsync(ct) ?? Task.CompletedTask;
        }
    }
}
