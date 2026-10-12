using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class OperatorIntentSubmissionOutput
{
    internal static IReadOnlyList<string> PrepareOperatorIntentArguments(
        IReadOnlyList<string> args, out bool emitAgentReadEnvelope)
    {
        emitAgentReadEnvelope = false;
        return CliPersistentStateRunner.IsInboxBackedGoalScopedTaskMutationCommand(args)
            ? StripJsonFlag(args, out emitAgentReadEnvelope)
            : args;
    }

    internal static IReadOnlyList<string> StripJsonFlag(
        IReadOnlyList<string> args, out bool emitAgentReadEnvelope)
    {
        emitAgentReadEnvelope = args.Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase));
        return emitAgentReadEnvelope
            ? args.Where(arg => !arg.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray()
            : args;
    }

    internal static void PrintTask(OperatorIntentRecord persisted, string orchestratorDirectory,
        string? suppliedGoalSelector, string goalId, string taskId, bool emitAgentReadEnvelope) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"selector={suppliedGoalSelector ?? "current"} goal={goalId} " +
            $"task={taskId} status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");

    internal static void PrintAnswer(OperatorIntentRecord persisted, string orchestratorDirectory,
        string goalId, bool emitAgentReadEnvelope) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"goal={goalId} status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).");

    internal static void PrintCancelDispatch(OperatorIntentRecord persisted, string orchestratorDirectory,
        string goalId, string taskId, string nextStepLine, bool emitAgentReadEnvelope) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"goal={goalId} task={taskId} status={persisted.Status}; " +
            $"poll with operator-intent-status {persisted.Id} (or add --wait).", nextStepLine);

    internal static void PrintWorkspace(OperatorIntentRecord persisted, string orchestratorDirectory,
        bool emitAgentReadEnvelope, TextWriter? output = null) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} scope=workspace status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).",
            output: output);

    internal static void PrintCriterionEvidence(OperatorIntentRecord persisted, string orchestratorDirectory,
        string goalId, string? targetLine, bool emitAgentReadEnvelope) =>
        Print(persisted, orchestratorDirectory, emitAgentReadEnvelope,
            CriterionEvidenceQueuedLine(persisted, goalId), targetLine);

    internal static void PrintCriterionEvidenceBatch(IReadOnlyList<OperatorIntentRecord> persisted,
        string orchestratorDirectory, string goalId, IReadOnlyList<string> targetLines, bool emitAgentReadEnvelope) =>
        Print(orchestratorDirectory, emitAgentReadEnvelope,
            emitAgentReadEnvelope ? "[" + string.Join(",", persisted.Select(intent => OperatorIntentAgentReadView.Render(intent))) + "]" : null,
            persisted.Select(intent => CriterionEvidenceQueuedLine(intent, goalId)).ToArray(), targetLines);

    private static string CriterionEvidenceQueuedLine(OperatorIntentRecord persisted, string goalId) =>
        $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} goal={goalId} " +
        $"status={persisted.Status}; poll with operator-intent-status {persisted.Id} (or add --wait).";

    private static void Print(OperatorIntentRecord persisted, string orchestratorDirectory,
        bool emitAgentReadEnvelope, string queuedLine, string? followUpLine = null, TextWriter? output = null) =>
        Print(orchestratorDirectory, emitAgentReadEnvelope,
            emitAgentReadEnvelope ? OperatorIntentAgentReadView.Render(persisted) : null,
            [queuedLine], followUpLine is null ? [] : [followUpLine], output);

    private static void Print(string orchestratorDirectory, bool emitAgentReadEnvelope,
        string? renderedDocument, IReadOnlyList<string> queuedLines, IReadOnlyList<string> followUpLines,
        TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        if (emitAgentReadEnvelope)
            writer.WriteLine(renderedDocument);
        else
            foreach (var queuedLine in queuedLines)
                writer.WriteLine(queuedLine);
        foreach (var followUpLine in followUpLines)
            (emitAgentReadEnvelope ? Console.Error : writer).WriteLine(followUpLine);
        if (!ConductorLoopLease.IsActive(orchestratorDirectory))
            (emitAgentReadEnvelope ? Console.Error : writer).WriteLine(ConductorLoopLease.InactiveWarning);
    }
}
