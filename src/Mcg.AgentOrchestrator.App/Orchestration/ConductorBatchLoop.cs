using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorBatchLoop
{
    internal const string StopFileName = ".conduct-stop";
    internal const int DefaultMaxVerifyRetries = 2;

    private readonly Action<AgentOrchestratorKernel> _sweep;

    public ConductorBatchLoop(Action<AgentOrchestratorKernel>? sweep = null)
    {
        _sweep = sweep ?? (_ => { });
    }

    public BatchLoopSummary Run(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        string stopFilePath,
        int? maxIterations = null,
        int maxVerifyRetries = DefaultMaxVerifyRetries)
    {
        var excludedGoals = new HashSet<string>(StringComparer.Ordinal);
        var retryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalTicks = 0;
        var totalAdvanced = 0;
        var totalHeld = 0;
        var totalEscalated = 0;
        var totalRetried = 0;
        var stopRequested = false;

        while (true)
        {
            if (IsStopRequested(stopFilePath))
            {
                stopRequested = true;
                Console.WriteLine($"[conduct --loop] Stop signal detected at tick {totalTicks + 1}; no new dispatches will be started.");
                break;
            }

            if (maxIterations.HasValue && totalTicks >= maxIterations.Value)
            {
                Console.WriteLine($"[conduct --loop] Max iterations ({maxIterations.Value}) reached after {totalTicks} ticks.");
                break;
            }

            _sweep(kernel);

            var eligible = kernel.Goals
                .Where(g => !excludedGoals.Contains(g.Id.Value))
                .ToArray();

            if (eligible.Length == 0)
            {
                Console.WriteLine($"[conduct --loop] All goals done or escalated; loop complete after {totalTicks} ticks.");
                break;
            }

            totalTicks++;
            var tickAdvanced = 0;
            var tickHeld = 0;
            var tickEscalated = 0;
            var tickRetried = 0;
            var tickDone = 0;

            foreach (var goal in eligible)
            {
                var result = driver.AdvanceOnce(goal, policy);

                // Auto-retry transient acceptance verification failures (up to maxVerifyRetries re-verifications)
                if (result.WasEscalated && IsTransientVerificationFailure(result))
                {
                    retryCounts.TryGetValue(goal.Id.Value, out var retries);
                    while (retries < maxVerifyRetries && result.WasEscalated && IsTransientVerificationFailure(result))
                    {
                        retries++;
                        retryCounts[goal.Id.Value] = retries;
                        tickRetried++;
                        Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {goal.Id.Value[..8]} acceptance flake (retry {retries}/{maxVerifyRetries})");
                        kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop auto-retry acceptance verification (attempt {retries}/{maxVerifyRetries})");
                        result = driver.AdvanceOnce(goal, policy);
                    }
                }

                var label = goal.Id.Value[..8];
                Console.WriteLine($"[conduct --loop] Tick {totalTicks}: {label} [{policy.Name}] → {FormatOutcome(result.Outcome)}");
                kernel.RecordGoalPolicyDecision(goal.Id, $"Batch loop tick {totalTicks}: {FormatOutcome(result.Outcome)}");

                if (result.WasExecuted)        { tickAdvanced++; }
                else if (result.IsHeld)        { tickHeld++; }
                else if (result.WasEscalated)  { tickEscalated++; excludedGoals.Add(goal.Id.Value); }
                else if (result.IsDone)        { tickDone++;      excludedGoals.Add(goal.Id.Value); }
            }

            totalAdvanced  += tickAdvanced;
            totalHeld      += tickHeld;
            totalEscalated += tickEscalated;
            totalRetried   += tickRetried;

            Console.WriteLine($"[conduct --loop] Tick {totalTicks} summary: advanced={tickAdvanced} held={tickHeld} escalated={tickEscalated} retried={tickRetried} done={tickDone}");

            if (tickAdvanced == 0 && tickDone == 0)
            {
                Console.WriteLine($"[conduct --loop] No progress in tick {totalTicks}; all eligible goals held or escalated.");
                break;
            }
        }

        return new BatchLoopSummary(totalTicks, totalAdvanced, totalHeld, totalEscalated, totalRetried, stopRequested);
    }

    private static bool IsTransientVerificationFailure(ConductorAdvanceResult result) =>
        result.Outcome is ConductorAdvanceOutcome.Escalated esc
        && esc.State == GoalLifecycleState.Verified
        && esc.Reason.Contains("Acceptance verification failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsStopRequested(string stopFilePath) =>
        !string.IsNullOrEmpty(stopFilePath) && File.Exists(stopFilePath);

    private static string FormatOutcome(ConductorAdvanceOutcome outcome) => outcome switch
    {
        ConductorAdvanceOutcome.Executed e  => $"executed from {e.FromState} — {e.Description}",
        ConductorAdvanceOutcome.Held h      => $"held at {h.State} — {h.Reason}",
        ConductorAdvanceOutcome.Escalated e => $"escalated at {e.State} — {e.Reason}",
        ConductorAdvanceOutcome.Done d      => $"done ({d.State})",
        _                                   => outcome.ToString()!
    };
}

public sealed record BatchLoopSummary(
    int Ticks,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    bool StopRequested);
