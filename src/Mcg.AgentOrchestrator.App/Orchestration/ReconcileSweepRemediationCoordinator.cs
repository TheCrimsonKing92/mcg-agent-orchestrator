using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum TerminalGoalRemedyExecutionDisposition
{
    Completed,
    Pending,
    Retryable
}

internal sealed record TerminalGoalRemedyExecutionResult(
    int ExitStatus,
    string Output,
    TerminalGoalRemedyExecutionDisposition Disposition = TerminalGoalRemedyExecutionDisposition.Completed)
{
    public bool Succeeded => Disposition == TerminalGoalRemedyExecutionDisposition.Completed && ExitStatus == 0;

    public static TerminalGoalRemedyExecutionResult Pending(string output) =>
        new(0, output, TerminalGoalRemedyExecutionDisposition.Pending);

    public static TerminalGoalRemedyExecutionResult Retryable(int exitStatus, string output) =>
        new(exitStatus, output, TerminalGoalRemedyExecutionDisposition.Retryable);
}

internal sealed record ReconcileSweepRemediationOutcome(
    IReadOnlyList<string> Events,
    bool RemedySucceeded);

internal sealed class ReconcileSweepRemediationCoordinator(
    IReconcileSweepRemediationStore store,
    ReconcileSweepOptions options,
    Func<TerminalGoalRemedy, TerminalGoalRemedyExecutionResult> executor,
    Func<TerminalGoalRemedy, string?> currentBranchHead)
{
    private static readonly TimeSpan AttemptStaleAfter = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, PendingAttempt> _pendingAttempts = new(StringComparer.Ordinal);

    public ReconcileSweepRemediationOutcome Process(TerminalGoalSweepResult sweep)
    {
        var events = new List<string>();
        var remedySucceeded = false;
        var polledStateKeys = PollPendingAttempts(events, ref remedySucceeded);

        foreach (var goal in sweep.Goals)
        {
            foreach (var blocker in goal.Blockers.Where(blocker => blocker.Kind != "stale-terminal-excluded"))
            {
                var remedy = blocker.Remedy;
                var stateKey = BuildStateKey(goal.GoalId.Value, blocker.Kind, blocker.Evidence, remedy);
                if (polledStateKeys.Contains(stateKey))
                {
                    continue;
                }
                var state = store.Observe(stateKey, goal.GoalId.Value, blocker.Kind, blocker.Evidence, remedy.RenderCommand());
                if (store.TryMarkBlockerEmitted(stateKey))
                {
                    events.Add(RenderBlocker(goal.GoalPrefix, blocker));
                }

                if (!IsAutoRunnable(blocker, remedy))
                {
                    if (store.TryMarkEscalationEmitted(stateKey))
                    {
                        events.Add(RenderEscalation(goal.GoalPrefix, blocker, "not-auto-runnable"));
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(state.InFlightOwner) &&
                    state.InFlightAt >= DateTimeOffset.UtcNow.Subtract(AttemptStaleAfter))
                {
                    var pendingResult = Execute(remedy);
                    if (pendingResult.Disposition != TerminalGoalRemedyExecutionDisposition.Pending)
                    {
                        CompleteAttempt(
                            events,
                            goal.GoalPrefix,
                            blocker,
                            stateKey,
                            state.InFlightOwner,
                            state.AttemptCount,
                            pendingResult);
                        remedySucceeded |= pendingResult.Succeeded;
                    }
                    else
                    {
                        _pendingAttempts[stateKey] = new PendingAttempt(
                            goal.GoalPrefix,
                            blocker,
                            remedy,
                            state.InFlightOwner,
                            state.AttemptCount);
                    }
                    continue;
                }

                var owner = $"{Environment.ProcessId}:{Guid.NewGuid():N}";
                var claim = store.TryClaimAttempt(stateKey, options.MaximumAttempts, owner);
                if (!claim.Claimed)
                {
                    if (claim.AttemptNumber >= options.MaximumAttempts && store.TryMarkEscalationEmitted(stateKey))
                    {
                        events.Add(RenderEscalation(goal.GoalPrefix, blocker, "attempt-budget-exhausted"));
                    }
                    continue;
                }

                events.Add(
                    $"SWEEP_REMEDY_ATTEMPT goal={goal.GoalPrefix} kind={blocker.Kind} attempt={claim.AttemptNumber} " +
                    $"remedy={remedy.Verb} command={JsonSerializer.Serialize(remedy.RenderCommand())} artifact={FormatArtifact(remedy.GateArtifact)}");

                if (!store.TryClaimAcceptanceLease(remedy.GoalId.Value, owner, AttemptStaleAfter))
                {
                    CompleteAttempt(
                        events,
                        goal.GoalPrefix,
                        blocker,
                        stateKey,
                        owner,
                        claim.AttemptNumber,
                        TerminalGoalRemedyExecutionResult.Retryable(
                            75,
                            "acceptance lease unavailable; another acceptance operation is active"));
                    continue;
                }

                var result = Execute(remedy);
                if (result.Disposition == TerminalGoalRemedyExecutionDisposition.Pending)
                {
                    _pendingAttempts[stateKey] = new PendingAttempt(
                        goal.GoalPrefix,
                        blocker,
                        remedy,
                        owner,
                        claim.AttemptNumber);
                    continue;
                }

                CompleteAttempt(events, goal.GoalPrefix, blocker, stateKey, owner, claim.AttemptNumber, result);
                remedySucceeded |= result.Succeeded;
            }
        }

        return new ReconcileSweepRemediationOutcome(events, remedySucceeded);
    }

    private HashSet<string> PollPendingAttempts(List<string> events, ref bool remedySucceeded)
    {
        var polledStateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (stateKey, pending) in _pendingAttempts.ToArray())
        {
            polledStateKeys.Add(stateKey);
            var result = Execute(pending.Remedy);
            if (result.Disposition == TerminalGoalRemedyExecutionDisposition.Pending)
            {
                continue;
            }

            CompleteAttempt(
                events,
                pending.GoalPrefix,
                pending.Blocker,
                stateKey,
                pending.Owner,
                pending.AttemptNumber,
                result);
            _pendingAttempts.Remove(stateKey);
            remedySucceeded |= result.Succeeded;
        }

        return polledStateKeys;
    }

    private TerminalGoalRemedyExecutionResult Execute(TerminalGoalRemedy remedy)
    {
        try
        {
            return executor(remedy);
        }
        catch (Exception ex)
        {
            return new TerminalGoalRemedyExecutionResult(1, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CompleteAttempt(
        List<string> events,
        string goalPrefix,
        TerminalGoalSweepBlocker blocker,
        string stateKey,
        string owner,
        int attemptNumber,
        TerminalGoalRemedyExecutionResult result)
    {
        var consumesAttempt = result.Disposition == TerminalGoalRemedyExecutionDisposition.Completed;
        store.CompleteAttempt(stateKey, owner, result.ExitStatus, result.Output, consumesAttempt);
        store.ReleaseAcceptanceLease(blocker.Remedy.GoalId.Value, owner);
        events.Add(
            $"SWEEP_REMEDY_RESULT goal={goalPrefix} kind={blocker.Kind} attempt={attemptNumber} " +
            $"exit={result.ExitStatus} output={JsonSerializer.Serialize(result.Output)}" +
            (consumesAttempt ? string.Empty : " retryable=true"));

        if (consumesAttempt &&
            !result.Succeeded &&
            attemptNumber >= options.MaximumAttempts &&
            store.TryMarkEscalationEmitted(stateKey))
        {
            events.Add(RenderEscalation(goalPrefix, blocker, "attempt-budget-exhausted"));
        }
    }

    private bool IsAutoRunnable(TerminalGoalSweepBlocker blocker, TerminalGoalRemedy remedy)
    {
        if (!options.AutoRemediationAllowlist.Contains(blocker.Kind) ||
            remedy.Verb != TerminalGoalRemedyVerb.Acceptance ||
            remedy.SafetyClass != TerminalGoalRemedySafetyClass.KnownSafeIdempotent ||
            remedy.GateArtifact is not { Outcome: "gate-passed" } artifact)
        {
            return false;
        }

        return string.Equals(
            currentBranchHead(remedy)?.Trim(),
            artifact.CandidateBranchSha.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildStateKey(
        string goalId,
        string blockerKind,
        string evidence,
        TerminalGoalRemedy remedy)
    {
        var canonical = string.Join('\n', [
            goalId,
            blockerKind,
            evidence,
            remedy.Verb.ToString(),
            remedy.RenderCommand(),
            remedy.GateArtifact?.Id ?? string.Empty,
            remedy.GateArtifact?.CandidateBranchSha ?? string.Empty
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string RenderBlocker(string goalPrefix, TerminalGoalSweepBlocker blocker) =>
        $"SWEEP_BLOCKER goal={goalPrefix} kind={blocker.Kind} evidence={JsonSerializer.Serialize(blocker.Evidence)} command={JsonSerializer.Serialize(blocker.Command)}";

    private static string RenderEscalation(string goalPrefix, TerminalGoalSweepBlocker blocker, string reason) =>
        $"SWEEP_ESCALATION goal={goalPrefix} kind={blocker.Kind} reason={reason} evidence={JsonSerializer.Serialize(blocker.Evidence)} command={JsonSerializer.Serialize(blocker.Command)}";

    private static string FormatArtifact(TerminalGoalGateArtifact? artifact) =>
        artifact is null ? "none" : JsonSerializer.Serialize(artifact.Id);

    private sealed record PendingAttempt(
        string GoalPrefix,
        TerminalGoalSweepBlocker Blocker,
        TerminalGoalRemedy Remedy,
        string Owner,
        int AttemptNumber);
}
