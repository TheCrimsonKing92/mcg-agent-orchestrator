using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum TerminalGoalRemedyVerb
{
    OperatorCommand,
    Acceptance
}

internal enum TerminalGoalRemedySafetyClass
{
    OperatorOnly,
    KnownSafeIdempotent
}

internal sealed record TerminalGoalGateArtifact(
    string Id,
    string CandidateBranchSha,
    string Outcome);

internal sealed record TerminalGoalRemedy(
    TerminalGoalRemedyVerb Verb,
    GoalId GoalId,
    string GoalPrefix,
    TerminalGoalRemedySafetyClass SafetyClass,
    IReadOnlyList<string> Arguments,
    TerminalGoalGateArtifact? GateArtifact = null)
{
    public static TerminalGoalRemedy Acceptance(
        GoalId goalId,
        string goalPrefix,
        TerminalGoalGateArtifact? gateArtifact) =>
        new(
            TerminalGoalRemedyVerb.Acceptance,
            goalId,
            goalPrefix,
            TerminalGoalRemedySafetyClass.KnownSafeIdempotent,
            [goalId.Value],
            gateArtifact);

    public static TerminalGoalRemedy OperatorOnly(GoalId goalId, string goalPrefix, string command) =>
        new(
            TerminalGoalRemedyVerb.OperatorCommand,
            goalId,
            goalPrefix,
            TerminalGoalRemedySafetyClass.OperatorOnly,
            [command]);

    public string RenderCommand() => Verb switch
    {
        TerminalGoalRemedyVerb.Acceptance => $"acceptance {GoalPrefix}",
        TerminalGoalRemedyVerb.OperatorCommand => Arguments.SingleOrDefault() ?? string.Empty,
        _ => throw new InvalidOperationException($"Unsupported terminal-goal remedy verb '{Verb}'.")
    };

    public IReadOnlyList<string> BuildInvocationArguments() => Verb switch
    {
        TerminalGoalRemedyVerb.Acceptance => ["acceptance", GoalId.Value],
        TerminalGoalRemedyVerb.OperatorCommand => [],
        _ => throw new InvalidOperationException($"Unsupported terminal-goal remedy verb '{Verb}'.")
    };
}
