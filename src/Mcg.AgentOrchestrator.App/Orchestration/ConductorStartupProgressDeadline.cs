namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorStartupProgressDeadline
{
    internal static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(10);

    private ConductorStartupProgressDeadline() { }

    internal readonly record struct Result(TimeSpan Delay, bool CeilingReached);

    internal static Result Compute(
        DateTimeOffset loopReadyAt, DateTimeOffset lastLineAt,
        DateTimeOffset now, TimeSpan silenceWindow)
    {
        var anchor = lastLineAt > loopReadyAt ? lastLineAt : loopReadyAt;
        var silenceRemaining = anchor + silenceWindow - now;
        var ceilingRemaining = loopReadyAt + Ceiling - now;
        var delay = silenceRemaining < ceilingRemaining ? silenceRemaining : ceilingRemaining;
        return new(delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
            ceilingRemaining <= TimeSpan.Zero);
    }
}
