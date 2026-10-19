using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CompletionBuildOriginPredicates
{
    internal sealed record Context(TaskSpec Task, GoalId GoalId, CompletionBuildEvidenceOperation Operation,
        string StatePath, string ExecutionDirectory, TaskDispatchRecord? Dispatch);
    internal sealed record Entry(string Name, Func<Context, (string Observed, string Expected)?> Evaluate);
    internal sealed record Failure(string PredicateName, string Observed, string Expected);

    internal static IReadOnlyList<Entry> Ordered { get; } = Array.AsReadOnly<Entry>(
    [
        new("OperationIdFormat", c => !Guid.TryParseExact(c.Operation.Id, "N", out _)
            ? Values(c.Operation.Id, "32-digit hex Guid (N format)") : null),
        new("PhaseDefined", c => !Enum.IsDefined(c.Operation.Phase)
            ? Values(((int)c.Operation.Phase).ToString(CultureInfo.InvariantCulture),
                string.Join(", ", Enum.GetNames<CompletionBuildEvidencePhase>())) : null),
        new("PendingCustodyConsistent", c => c.Operation.IsPending && c.Operation.CustodyReleased
            ? Values($"{c.Operation.Phase}; CustodyReleased={c.Operation.CustodyReleased}", "CustodyReleased=False while pending") : null),
        new("SuccessExitCodeZero", c => c.Operation.Phase == CompletionBuildEvidencePhase.Success && c.Operation.BuildExitCode != 0
            ? Values(c.Operation.BuildExitCode?.ToString(CultureInfo.InvariantCulture), "0") : null),
        new("GoalIdMatches", c => c.Operation.GoalId != c.GoalId.Value
            ? Values(c.Operation.GoalId, c.GoalId.Value) : null),
        new("TaskIdMatches", c => c.Operation.TaskId != c.Task.Id.Value
            ? Values(c.Operation.TaskId, c.Task.Id.Value) : null),
        new("DispatchPresent", c => c.Dispatch is null
            ? Values("0", "1 dispatch at DispatchAt") : null),
        new("DispatchIdMatches", c =>
        {
            var expected = BackgroundDispatchRunner.BuildDispatchId(c.GoalId, c.Task.Id, c.Dispatch!);
            return expected != c.Operation.DispatchId ? Values(c.Operation.DispatchId, expected) : null;
        }),
        new("WorkerNotRunning", c => c.Operation.WorkerProcess.IsRunning
            ? Values("True", "False") : null),
        new("WorkerCommandMatches", c => c.Operation.WorkerProcess.Command != c.Dispatch!.Command
            ? Values(CommandIdentity(c.Operation.WorkerProcess.Command), CommandIdentity(c.Dispatch.Command)) : null),
        new("WorkerWorkingDirectoryMatches", c =>
            !string.Equals(c.Operation.WorkerProcess.WorkingDirectory, c.Operation.WorktreeRoot, StringComparison.OrdinalIgnoreCase)
                ? Values(c.Operation.WorkerProcess.WorkingDirectory, c.Operation.WorktreeRoot) : null),
        new("DispatchWorkingDirectoryMatches", c =>
        {
            var observed = Path.GetFullPath(c.Dispatch!.WorkingDirectory);
            return !string.Equals(observed, c.Operation.WorktreeRoot, StringComparison.OrdinalIgnoreCase)
                ? Values(observed, c.Operation.WorktreeRoot) : null;
        }),
        new("StatePathMatches", c =>
        {
            var observed = Path.GetFullPath(c.Operation.StatePath);
            var expected = Path.GetFullPath(c.StatePath);
            return !string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase) ? Values(observed, expected) : null;
        }),
        new("ExecutionDirectoryMatches", c =>
        {
            var observed = Path.GetFullPath(c.Operation.ExecutionDirectory);
            var expected = Path.GetFullPath(c.ExecutionDirectory);
            return !string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase) ? Values(observed, expected) : null;
        }),
        new("ProjectsPresent", c => c.Operation.Projects.Count == 0 ? Values("0", ">0") : null),
        new("ProjectsNormalized", c =>
        {
            var normalized = CompletionBuildEvidenceCoordinator.NormalizeProjects(c.Operation.Projects);
            if (normalized.SequenceEqual(c.Operation.Projects)) return null;
            var firstDifference = Enumerable.Range(0, Math.Min(normalized.Length, c.Operation.Projects.Count))
                .FirstOrDefault(i => normalized[i] != c.Operation.Projects[i], -1);
            return Values($"count={c.Operation.Projects.Count}; first differing index={firstDifference}",
                $"normalized count={normalized.Length}; first differing index=-1");
        }),
        new("TreeDigestPresent", c => string.IsNullOrWhiteSpace(c.Operation.TreeDigest)
            ? Values(c.Operation.TreeDigest, "non-blank digest") : null),
        new("ResultPathMatches", c =>
        {
            var expected = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(c.GoalId),
                "completion-build-evidence", c.Operation.Id + ".result.json");
            return !string.Equals(c.Operation.ResultPath, expected, StringComparison.OrdinalIgnoreCase)
                ? Values(c.Operation.ResultPath, expected) : null;
        })
    ]);

    internal static Failure? FirstFailure(TaskSpec task, GoalId goalId, CompletionBuildEvidenceOperation operation,
        string statePath, string executionDirectory)
    {
        // Keep the eager SingleOrDefault: duplicate timestamps retain their original exception.
        var dispatch = task.DispatchHistory.SingleOrDefault(d => d.DispatchedAt == operation.DispatchAt);
        var context = new Context(task, goalId, operation, statePath, executionDirectory, dispatch);
        foreach (var predicate in Ordered)
            if (predicate.Evaluate(context) is { } values)
                return new(predicate.Name, values.Observed, values.Expected);
        return null;
    }

    private static (string Observed, string Expected) Values(string? observed, string? expected) =>
        (CompletionBuildOriginRejectedException.Bound(observed), CompletionBuildOriginRejectedException.Bound(expected));

    // Commands may contain arbitrary text; expose only a count and a digest identity.
    private static string CommandIdentity(string? command) => command is null ? "<null>" :
        $"length={command.Length}; sha256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command)))[..12]}";
}
