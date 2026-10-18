using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProviderBudgetProbeAdmission
{
    internal static void Apply(AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<TaskId> batch,
        Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight, DateTimeOffset now)
    {
        var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var taskId in batch)
        {
            var task = kernel.GetTask(goal.Id, taskId);
            if (task.Status != WorkTaskStatus.Assigned) continue;
            var blocked = preflight(goal, task, kernel.Goals).Findings
                .Where(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (blocked.Length != 1 || !blocked[0].StartsWith("blocked: code=provider-budget-exhausted;", StringComparison.Ordinal)) continue;
            // The binding is derived from dispatch history; the diagnostic only identifies which hold blocked this task.
            var bindings = kernel.Goals.SelectMany(item => item.Tasks).SelectMany(item => item.DispatchHistory)
                .Select(dispatch => (Provider: dispatch.ProviderName ?? dispatch.WorkerName, Credential: dispatch.ClaudeCredentialSourceDirectory)).Distinct();
            foreach (var binding in bindings)
            {
                if (!DispatchFailureClassifier.TryGetProviderBudgetExhaustionHold(kernel.Goals, binding.Provider, binding.Credential, out var hold) ||
                    !blocked[0].Contains($"binding {hold.BindingKey} (", StringComparison.OrdinalIgnoreCase) || admitted.Contains(hold.BindingKey)) continue;
                var schedule = ProviderBudgetHoldSchedules.Evaluate(kernel.Goals, hold, now);
                if (now < schedule.NextProbeAt || schedule.ProbeInFlight || schedule.AdmittedTaskId is not null) break;
                kernel.RecordTaskNote(goal.Id, task.Id, ProviderBudgetHoldSchedules.FormatProbe(
                    new(hold.BindingKey, schedule.NextProbeAt, schedule.Attempt, schedule.ResetSource)));
                admitted.Add(hold.BindingKey);
                break;
            }
        }
    }
}
