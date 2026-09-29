using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private void ReapRecordedGateChildBeforeManagedDotnetCommand(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        GoalId? goalId,
        int? stableSlotIndex)
    {
        var seam = _testOverrides.GateChildReapSeamForTests ?? DefaultGateChildReapSeam.Instance;
        var budget = _testOverrides.GateChildExitConfirmationBudget ?? TimeSpan.FromSeconds(30);
        GateChildReapOutcome? selected = null;
        foreach (var heartbeatPath in ResolveGateHeartbeatPathsForReap(check, environment, stableSlotIndex))
        {
            var outcome = EvaluateRecordedGateChild(heartbeatPath, environment, goalId, stableSlotIndex, seam, budget);
            if (outcome.Kind != GateChildReapOutcomeKind.NotApplicable)
            {
                selected = outcome;
                break;
            }

            if (selected is null || selected.Reason == "no-heartbeat")
                selected = outcome;
        }

        selected ??= new GateChildReapOutcome(null, GateChildReapOutcomeKind.NotApplicable, "no-heartbeat");
        var line = $"GATE_CHILD_REAP check={QuoteProgressToken(check.Name)} " +
            $"pid={selected.ChildPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} " +
            $"outcome={selected.Kind switch
            {
                GateChildReapOutcomeKind.Reaped => "reaped",
                GateChildReapOutcomeKind.StillAlive => "still-alive",
                _ => "not-applicable"
            }} reason={selected.Reason}";
        Console.WriteLine(line);
        Console.Out.Flush();
        if (selected.Kind == GateChildReapOutcomeKind.StillAlive)
            throw new GateChildStillAliveException(line, environment.ArtifactsPath, environment.LeaseId);
    }

    private IEnumerable<string> ResolveGateHeartbeatPathsForReap(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        int? stableSlotIndex)
    {
        yield return ResolveGateHeartbeatPath(check, environment, stableSlotIndex, worktreePath: null);

        if (string.IsNullOrWhiteSpace(AcceptanceAttemptResultsPrefix) ||
            _invocationContext is not { Ordinal: > 0 } invocation)
            yield break;

        for (var ordinal = invocation.Ordinal - 1; ordinal >= 0; ordinal--)
            yield return ResolveGateHeartbeatPath(
                check, environment, stableSlotIndex, worktreePath: null, invocationOrdinal: ordinal);
    }

    private static GateChildReapOutcome EvaluateRecordedGateChild(
        string heartbeatPath,
        DotnetBuildEnvironment environment,
        GoalId? goalId,
        int? stableSlotIndex,
        IGateChildReapSeam seam,
        TimeSpan budget)
    {
        static GateChildReapOutcome Skip(string reason, int? pid = null) =>
            new(pid, GateChildReapOutcomeKind.NotApplicable, reason);

        if (!File.Exists(heartbeatPath))
            return Skip("no-heartbeat");

        GateHeartbeatSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<GateHeartbeatSnapshot>(
                File.ReadAllText(heartbeatPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return Skip("unreadable-heartbeat");
        }

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.State))
            return Skip("unreadable-heartbeat");
        if (snapshot.ChildPid is not { } childPid)
            return Skip("no-child-pid");
        if (stableSlotIndex.HasValue && snapshot.SlotIndex != stableSlotIndex)
            return Skip("slot-mismatch", childPid);
        if (goalId is not null && !string.IsNullOrWhiteSpace(snapshot.GoalId) &&
            !snapshot.GoalId.Equals(goalId.Value, StringComparison.Ordinal))
            return Skip("goal-mismatch", childPid);
        if (!snapshot.State.Equals("running", StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - snapshot.LastObservedAt > TimeSpan.FromMinutes(5))
            return Skip("stale-snapshot", childPid);
        if (snapshot.CommandLine is not null &&
            !snapshot.CommandLine.Contains(environment.ArtifactsPath, StringComparison.OrdinalIgnoreCase))
            return Skip("artifacts-path-mismatch", childPid);

        using var child = seam.TryOpen(childPid, snapshot.StartedAt, snapshot.LastObservedAt);
        if (child is null)
            return Skip("not-running", childPid);

        // The kill helper may time out even when the recorded handle exits shortly after it returns.
        // Confirmation is based on that handle, not the helper's boolean or a new lookup by pid.
        _ = child.Kill();
        return child.WaitForExit(budget)
            ? new GateChildReapOutcome(childPid, GateChildReapOutcomeKind.Reaped, "exit-confirmed")
            : new GateChildReapOutcome(childPid, GateChildReapOutcomeKind.StillAlive, "exit-unconfirmed-within-budget");
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedDotnetCheckAsync(
        AcceptanceManifestCheck check, string[] arguments, string worktreePath, GoalId? goalId,
        int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease, string attemptName,
        CancellationToken cancellationToken, Action<DotnetBuildEnvironment>? afterLeasePrepared = null,
        DotnetBuildEnvironment? executionEnvironment = null, bool waitForPermit = false)
    {
        try
        {
            return await RunManagedDotnetCheckCoreAsync(check, arguments, worktreePath, goalId,
                stableSlotIndex, stableSlotLease, attemptName, cancellationToken, afterLeasePrepared,
                executionEnvironment, waitForPermit).ConfigureAwait(false);
        }
        catch (GateChildStillAliveException ex)
        {
            return (BuildGateChildStillAliveResult(check, ex), false);
        }
    }

    private async Task<(AcceptanceCheckResult Result, bool Retried)> RunManagedMtpExecutableCheckAsync(
        AcceptanceManifestCheck check, string worktreePath, GoalId? goalId, int? stableSlotIndex,
        DotnetBuildEnvironmentLease? stableSlotLease, DotnetBuildEnvironment? executableEnvironment,
        string attemptName, CancellationToken cancellationToken,
        string? testResultsDirectoryOverride = null)
    {
        try
        {
            return await RunManagedMtpExecutableCheckCoreAsync(check, worktreePath, goalId,
                stableSlotIndex, stableSlotLease, executableEnvironment, attemptName,
                cancellationToken, testResultsDirectoryOverride).ConfigureAwait(false);
        }
        catch (GateChildStillAliveException ex)
        {
            return (BuildGateChildStillAliveResult(check, ex), false);
        }
    }

    private AcceptanceCheckResult BuildGateChildStillAliveResult(
        AcceptanceManifestCheck check, GateChildStillAliveException exception) =>
        new(check.Name, false, null, exception.ProgressLine, exception.ArtifactsPath,
            "goal-acceptance-verifier", exception.LeaseId,
            ResultSummary: exception.ProgressLine,
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference,
            TestResultRunOrdinal: _invocationContext?.Ordinal ?? 0);

    private static AcceptanceCheckResult DecorateVerdictWithGateChildReap(
        AcceptanceCheckResult verdict, AcceptanceCheckResult probe)
    {
        if (probe.ResultSummary is not { } line || !line.Contains("GATE_CHILD_REAP ", StringComparison.Ordinal) ||
            !line.Contains(" outcome=still-alive ", StringComparison.Ordinal))
            return verdict;

        return verdict with
        {
            ResultSummary = PrefixResultSummary(line, verdict.ResultSummary),
            FailureClassification = AcceptanceFailureClassifications.IsEnvironmentalApparatus(verdict.FailureClassification)
                ? verdict.FailureClassification
                : AcceptanceFailureClassifications.GateEnvironmentInterference
        };
    }
}
