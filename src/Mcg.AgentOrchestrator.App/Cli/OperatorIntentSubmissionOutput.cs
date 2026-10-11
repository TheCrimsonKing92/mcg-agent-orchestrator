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

    private static void Print(OperatorIntentRecord persisted, string orchestratorDirectory,
        bool emitAgentReadEnvelope, string queuedLine, string? followUpLine = null)
    {
        Console.WriteLine(emitAgentReadEnvelope ? OperatorIntentAgentReadView.Render(persisted) : queuedLine);
        if (followUpLine is not null)
            (emitAgentReadEnvelope ? Console.Error : Console.Out).WriteLine(followUpLine);
        if (!ConductorLoopLease.IsActive(orchestratorDirectory))
            (emitAgentReadEnvelope ? Console.Error : Console.Out).WriteLine(ConductorLoopLease.InactiveWarning);
    }
}
