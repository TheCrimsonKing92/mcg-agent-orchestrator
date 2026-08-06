using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TerminalGoalRemedyExecutionResult(int ExitStatus, string Output)
{
    public bool Succeeded => ExitStatus == 0;
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
    public ReconcileSweepRemediationOutcome Process(TerminalGoalSweepResult sweep)
    {
        var events = new List<string>();
        var remedySucceeded = false;

        foreach (var goal in sweep.Goals)
        {
            foreach (var blocker in goal.Blockers.Where(blocker => blocker.Kind != "stale-terminal-excluded"))
            {
                var remedy = blocker.Remedy;
                var stateKey = BuildStateKey(goal.GoalId.Value, blocker.Kind, blocker.Evidence, remedy);
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

                TerminalGoalRemedyExecutionResult result;
                try
                {
                    result = executor(remedy);
                }
                catch (Exception ex)
                {
                    result = new TerminalGoalRemedyExecutionResult(1, $"{ex.GetType().Name}: {ex.Message}");
                }

                store.CompleteAttempt(stateKey, owner, result.ExitStatus, result.Output);
                events.Add(
                    $"SWEEP_REMEDY_RESULT goal={goal.GoalPrefix} kind={blocker.Kind} attempt={claim.AttemptNumber} " +
                    $"exit={result.ExitStatus} output={JsonSerializer.Serialize(result.Output)}");
                remedySucceeded |= result.Succeeded;

                if (!result.Succeeded &&
                    claim.AttemptNumber >= options.MaximumAttempts &&
                    store.TryMarkEscalationEmitted(stateKey))
                {
                    events.Add(RenderEscalation(goal.GoalPrefix, blocker, "attempt-budget-exhausted"));
                }
            }
        }

        return new ReconcileSweepRemediationOutcome(events, remedySucceeded);
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
}
