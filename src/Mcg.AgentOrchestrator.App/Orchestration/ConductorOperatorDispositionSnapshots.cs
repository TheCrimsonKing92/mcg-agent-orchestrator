using System.Text.Json;
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
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

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

    public static GoalOperatorDisposition? TryReadLatestForGoal(string runEventStorePath, Goal goal)
    {
        if (string.IsNullOrWhiteSpace(runEventStorePath) || !File.Exists(runEventStorePath))
        {
            return null;
        }

        try
        {
            var records = new SqliteRunEventStore(runEventStorePath)
                .ReadSinceAsync(maxCount: 2000)
                .GetAwaiter()
                .GetResult();
            return TryFindLatestForGoal(records, goal);
        }
        catch
        {
            return null;
        }
    }

    public static GoalOperatorDisposition? TryFindLatestForGoal(IEnumerable<RunEventRecord> records, Goal goal)
    {
        foreach (var record in records
            .Where(record => record.EventType == RunEventTypes.ConductorTick)
            .OrderByDescending(record => record.Sequence))
        {
            foreach (var snapshot in ReadSnapshots(record).Reverse())
            {
                if (snapshot.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return ToDisposition(goal, snapshot);
                }
            }
        }

        return null;
    }

    internal static IReadOnlyList<ConductorOperatorDispositionSnapshot> ReadSnapshots(RunEventRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.PayloadJson))
        {
            return [];
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ConductorTickDispositionPayload>(
                record.PayloadJson,
                CaseInsensitiveJson);
            return payload?.OperatorDispositions ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static GoalOperatorDisposition ToDisposition(Goal goal, ConductorOperatorDispositionSnapshot snapshot) =>
        new(
            goal.Id,
            snapshot.State,
            snapshot.Confidence,
            snapshot.Reason,
            snapshot.NextSafeCommand,
            snapshot.FreshAt,
            snapshot.Blockers.ToList(),
            snapshot.Evidence.Select(ToEvidence).ToList(),
            snapshot.Dispatches.Select(ToDispatchDisposition).ToList());

    private static DispatchOperatorDisposition ToDispatchDisposition(ConductorDispatchOperatorDispositionSnapshot snapshot) =>
        new(
            new TaskId(snapshot.TaskId),
            snapshot.Role,
            snapshot.TaskStatus,
            snapshot.State,
            snapshot.Confidence,
            snapshot.Reason,
            snapshot.NextSafeCommand,
            snapshot.FreshAt,
            snapshot.Blockers.ToList(),
            snapshot.Evidence.Select(ToEvidence).ToList(),
            DispatchState: null);

    private static OperatorEvidencePointer ToEvidence(ConductorOperatorEvidenceSnapshot snapshot) =>
        new(snapshot.Kind, snapshot.Path, snapshot.Detail);

    private sealed record ConductorTickDispositionPayload(
        IReadOnlyList<ConductorOperatorDispositionSnapshot>? OperatorDispositions);
}
