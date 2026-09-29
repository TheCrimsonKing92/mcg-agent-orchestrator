namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class ConductorLoopHandoff
{
    private static (Func<DateTimeOffset> Clock, Action<TimeSpan> PollWait) ResolveVerificationTiming(
        ConductLoopHandoffOptions options) =>
        options.VerificationClock is not null && options.VerificationPollWait is not null
            ? (options.VerificationClock, options.VerificationPollWait)
            : (static () => DateTimeOffset.UtcNow, static interval => Thread.Sleep(interval));
}
