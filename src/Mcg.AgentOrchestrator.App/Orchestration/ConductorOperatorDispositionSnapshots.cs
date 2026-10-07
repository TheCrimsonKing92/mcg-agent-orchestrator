using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record ConductorOperatorDispositionSnapshot(
    string GoalId,
    OperatorDispositionState State,
    OperatorDispositionConfidence Confidence,
    string Reason,
    string NextSafeCommand,
    DateTimeOffset FreshAt,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<ConductorOperatorEvidenceSnapshot> Evidence,
    IReadOnlyList<ConductorDispatchOperatorDispositionSnapshot> Dispatches)
{
    public static ConductorOperatorDispositionSnapshot From(GoalOperatorDisposition disposition) =>
        new(
            disposition.GoalId.Value,
            disposition.State,
            disposition.Confidence,
            disposition.Reason,
            disposition.NextSafeCommand,
            disposition.FreshAt,
            disposition.Blockers,
            disposition.Evidence.Select(ConductorOperatorEvidenceSnapshot.From).ToList(),
            disposition.Dispatches.Select(ConductorDispatchOperatorDispositionSnapshot.From).ToList());
}

public sealed record ConductorDispatchOperatorDispositionSnapshot(
    string TaskId,
    AgentRole Role,
    WorkTaskStatus TaskStatus,
    OperatorDispositionState State,
    OperatorDispositionConfidence Confidence,
    string Reason,
    string NextSafeCommand,
    DateTimeOffset? FreshAt,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<ConductorOperatorEvidenceSnapshot> Evidence)
{
    public static ConductorDispatchOperatorDispositionSnapshot From(DispatchOperatorDisposition disposition) =>
        new(
            disposition.TaskId.Value,
            disposition.Role,
            disposition.TaskStatus,
            disposition.State,
            disposition.Confidence,
            disposition.Reason,
            disposition.NextSafeCommand,
            disposition.FreshAt,
            disposition.Blockers,
            disposition.Evidence.Select(ConductorOperatorEvidenceSnapshot.From).ToList());
}

public sealed record ConductorOperatorEvidenceSnapshot(string Kind, string Path, string Detail)
{
    public static ConductorOperatorEvidenceSnapshot From(OperatorEvidencePointer pointer) =>
        new(pointer.Kind, pointer.Path, pointer.Detail);
}

public static class ConductorOperatorDispositionSnapshots
{
    public static IReadOnlyList<ConductorOperatorDispositionSnapshot> Build(
        AgentOrchestratorKernel kernel,
        string? executionDirectory)
    {
        return Build(kernel, executionDirectory, ProcessCommandLines.SnapshotOperation);
    }

    internal static IReadOnlyList<ConductorOperatorDispositionSnapshot> Build(
        AgentOrchestratorKernel kernel,
        string? executionDirectory,
        Func<ProcessCommandLineSnapshot> createCommandLineSnapshot)
    {
        var surface = new GoalOperatorDispositionSurface();
        var commandLineSnapshot = createCommandLineSnapshot();
        return kernel.Goals
            .Select(goal => surface.Evaluate(
                goal,
                kernel.BuildMonitor(goal.Id).PendingHumanInputCount,
                kernel.BuildVerificationGate(goal.Id).IsSatisfied,
                executionDirectory,
                commandLineSnapshot,
                skipTerminalDispatchEvaluation: true,
                skipInactiveDispatchEvaluation: true))
            .Select(ConductorOperatorDispositionSnapshot.From)
            .ToList();
    }
}
