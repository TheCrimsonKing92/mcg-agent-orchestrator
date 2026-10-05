namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ProcessOutputDrainPolicy(
    TimeSpan Completion,
    TimeSpan CancellationGrace)
{
    public static ProcessOutputDrainPolicy Default { get; } =
        new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(2));

    public ProcessOutputDrainPolicy Validate()
    {
        if (Completion <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Completion), "Drain completion timeout must be positive.");
        }

        if (CancellationGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CancellationGrace), "Drain cancellation grace must be positive.");
        }

        return this;
    }
}
