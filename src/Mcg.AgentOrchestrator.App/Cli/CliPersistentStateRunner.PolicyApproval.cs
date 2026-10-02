using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool SubmitPolicyChangeApprovalIntent(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal,
        OperatorIntentSubmissionSource submissionSource)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        if (args.Count < 4 || args[1].StartsWith("--", StringComparison.Ordinal) ||
            !AcceptancePolicyChangeDecision.IsFullSha(args[2]))
            throw new ArgumentException(CliCommandHelp.ApprovePolicyChangeUsage);
        var textFile = ResolveFlagValue(args, "--text-file")
            ?? throw new ArgumentException(CliCommandHelp.ApprovePolicyChangeUsage);
        var reason = File.ReadAllText(textFile, System.Text.Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Approval reason file cannot be empty.");
        var attribution = ResolveOperatorIntentAttribution(args, submissionSource);
        if (attribution.ActorKind != OperatorActorKind.Human || attribution.AuthenticationAssurance != "local-process")
            throw new InvalidOperationException("Acceptance policy approval requires a human local operator.");
        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoal?.Id.Value, args[1]);
        var intentId = Guid.NewGuid().ToString("N");
        var record = new OperatorIntentRecord(
            intentId, ResolveFlagValue(args, "--idempotency-key") ?? intentId,
            OperatorIntentVerbs.ApprovePolicyChange, goalId.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(args[2].ToLowerInvariant(), reason), OperatorIntentJson.Options),
            [Path.GetFullPath(textFile)], attribution.Actor, attribution.Channel,
            attribution.AuthenticationAssurance, DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
        var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(record).GetAwaiter().GetResult();
        Console.WriteLine($"Operator intent queued: id={persisted.Id} verb={persisted.Verb} goal={goalId.Value} status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory))
            Console.WriteLine(ConductorLoopLease.InactiveWarning);
        return false;
    }
}
