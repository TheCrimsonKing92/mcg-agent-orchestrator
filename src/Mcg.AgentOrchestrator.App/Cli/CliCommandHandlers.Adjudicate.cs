using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record PreparedOperatorAdjudication(
    string Shape,
    string Text,
    IReadOnlyList<string> EvidenceReferences,
    string WorkingDirectory,
    string? Cause);

internal static partial class CliCommandHandlers
{
    private static GoalScopedTaskMutationCommand PrepareAdjudicationMutation(
        IReadOnlyList<string> parts,
        bool hasInlineGoalPrefix,
        OrchestratorWorkspace workspace)
    {
        var taskIndex = ResolveGoalScopedTaskArgumentIndex(parts, hasInlineGoalPrefix, CliCommandHelp.AdjudicateUsage);
        var shapeIndex = taskIndex + 1;
        RequireRemainingArgument(parts, shapeIndex, CliCommandHelp.AdjudicateUsage);
        var textFile = GetFlagValue(parts, "--text-file")
            ?? throw new ArgumentException($"adjudicate requires --text-file <path>. {CliCommandHelp.AdjudicateUsage}");
        if (!File.Exists(textFile))
            throw new FileNotFoundException($"Adjudication text file not found: {textFile}", textFile);

        var prepared = new PreparedOperatorAdjudication(
            parts[shapeIndex],
            File.ReadAllText(textFile, System.Text.Encoding.UTF8),
            GetAdjudicationEvidence(parts),
            workspace.RootDirectory,
            GetFlagValue(parts, "--cause"));
        return new GoalScopedTaskMutationCommand(
            OperatorIntentVerbs.Adjudicate,
            parts,
            Text: null,
            ProgressStatus: null,
            ManualVerification: null,
            RetryRoundKind: null,
            RetryPolicy: AutonomyPolicy.Default,
            Adjudication: prepared);
    }

    internal static AdjudicateOperatorIntentPayload BuildAdjudicationPayload(
        GoalScopedTaskMutationCommand command,
        OrchestratorWorkspace workspace,
        GoalId goalId)
    {
        var prepared = command.Adjudication
            ?? throw new InvalidOperationException("Prepared adjudicate command is missing its typed payload.");
        var stateDbPath = Path.Combine(workspace.OrchestratorDirectory, "state.db");
        var expectedGoalStateVersion = SqliteOrchestratorStateRepository
            .TryLoadGoalStateVersionAsync(stateDbPath, goalId.Value)
            .GetAwaiter()
            .GetResult()
            ?? throw new InvalidOperationException($"Goal state version is unavailable for '{goalId.Value}'.");
        return new AdjudicateOperatorIntentPayload(
            prepared.Shape,
            prepared.Text,
            prepared.EvidenceReferences,
            expectedGoalStateVersion,
            prepared.WorkingDirectory,
            prepared.Cause);
    }

    internal static OperatorActorKind ParseOperatorActorKind(string? value)
    {
        if (value is null)
            return OperatorActorKind.Human;
        if (!Enum.TryParse<OperatorActorKind>(value, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            throw new ArgumentException("--actor-kind must be Human or Agent.");
        return kind;
    }

    private static IReadOnlyList<string> GetAdjudicationEvidence(IReadOnlyList<string> parts)
    {
        var evidence = new List<string>();
        for (var index = 1; index < parts.Count; index++)
        {
            if (!parts[index].Equals("--evidence", StringComparison.OrdinalIgnoreCase))
                continue;
            while (++index < parts.Count && !parts[index].StartsWith("--", StringComparison.Ordinal))
                evidence.Add(parts[index]);
            index--;
        }
        return evidence.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray();
    }
}
