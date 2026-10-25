using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class OwnerConsoleControllerFactory
{
    internal static OwnerConsoleScreenController Create(IOwnerQuestionSource questions, IOwnerAnswerSubmitter answers,
        IOwnerConsoleDialogs dialogs, IOrchestratorStateQueries state, IGoalEventTail tail,
        IOwnerConsoleConductor conductor, IOwnerConsoleDigestReport digest,
        IOwnerDigestSummary summary, TimeProvider clock, IOwnerConsoleEpicSource? epics = null,
        AnswerIntentStatusTracker? answerTracking = null, OwnerQuestionResolutionReader? resolutions = null,
        IOwnerConsoleNotifier? notifier = null, OwnerConsoleNotificationSettings? notifications = null,
        Func<OwnerFleetViewModel>? fleet = null, Func<string, OwnerLandingsPage>? landings = null,
        OwnerGoalDetailQuery? goalDetail = null) =>
        new OwnerConsoleScreenController(questions, answers, dialogs, goalDetail ?? new OwnerGoalDetailQuery(state, tail),
            conductor, digest, summary, clock, epics, answerTracking, resolutions, notifier, notifications, fleet, landings);
}
