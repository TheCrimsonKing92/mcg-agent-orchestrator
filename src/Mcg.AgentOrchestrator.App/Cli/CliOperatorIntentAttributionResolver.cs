using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliOperatorIntentAttributionResolver
{
    internal static CliPersistentStateRunner.OperatorIntentAttribution Resolve(
        CliPersistentStateRunner.OperatorIntentSubmissionSource submissionSource,
        string? actor,
        OperatorActorKind actorKind) => submissionSource switch
    {
        CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli => new(
            actor ?? "operator",
            "cli",
            "local-process",
            actorKind),
        CliPersistentStateRunner.OperatorIntentSubmissionSource.Discord => new(
            actor ?? throw new ArgumentException(
                "Discord operator intent submissions require an authenticated --operator-actor."),
            "discord",
            "discord-operator-allowlist",
            actorKind),
        _ => throw new ArgumentOutOfRangeException(
            nameof(submissionSource),
            submissionSource,
            "Unsupported operator intent submission source.")
    };
}
