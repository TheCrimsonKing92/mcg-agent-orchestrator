namespace Mcg.AgentOrchestrator.Core;

public static class TaskGraphRepair
{
    // Simulate before publishing anything, including timeline or delegation changes.
    // Positional order is the task prerequisite contract; all old predecessors are retained.
    public static void Apply(AgentOrchestratorKernel kernel, GoalId goalId, TaskGraphRepairOperatorIntentPayload plan,
        IReadOnlySet<string>? admittedTaskIds = null)
    {
        var goal = kernel.GetGoal(goalId);

        if (plan.Version != 1 || plan.Insertions is null || plan.Insertions.Count == 0)
            throw new TaskGraphRepairRejectedException("invalid-plan", null, "Version 1 requires a nonempty insertions array.");
        var simulated = goal.Tasks.Select(task => (task.RequiredRole, task.Id.Value)).ToList();
        var parsed = new List<(AgentRole Role, string Description, AgentRole? Before)>();
        for (var i = 0; i < plan.Insertions.Count; i++)
        {
            var item = plan.Insertions[i];
            if (item is null || !TryRole(item.Role, out var role) || string.IsNullOrWhiteSpace(item.Description))
                throw new TaskGraphRepairRejectedException("invalid-insertion", i, "Supply a defined role and a nonempty description.");
            AgentRole? before = null;
            var index = simulated.Count;
            if (item.BeforeRole is not null)
            {
                if (!TryRole(item.BeforeRole, out var target))
                    throw new TaskGraphRepairRejectedException("invalid-target-role", i, item.BeforeRole);
                var matches = simulated.Select((entry, at) => (entry, at)).Where(pair => pair.entry.RequiredRole == target).ToArray();
                if (matches.Length != 1)
                    throw new TaskGraphRepairRejectedException(matches.Length == 0 ? "missing-target" : "ambiguous-target", i,
                        $"beforeRole={target}; matches={matches.Length}; placement requires one target.");
                before = target;
                index = matches[0].at;
            }
            // Even a completed/admitted downstream task cannot gain a new predecessor.
            if (simulated.Skip(index).Any(entry => admittedTaskIds?.Contains(entry.Value) == true ||
                goal.Tasks.Any(task => task.Id.Value == entry.Value &&
                    (task.LastProcess is not null || task.LastExecution is not null ||
                     task.Status is WorkTaskStatus.Running or WorkTaskStatus.Completed))))
                throw new TaskGraphRepairRejectedException("owned-task-predecessor", i,
                    "Insertion would add a predecessor to an admitted, running or completed task; its ownership is preserved.");
            if (goal.Tasks.Any(task => SdlcTaskDependencies.IsEarlierStage(role, task.RequiredRole) &&
                (admittedTaskIds?.Contains(task.Id.Value) == true || task.LastProcess is not null || task.LastExecution is not null ||
                 task.Status is WorkTaskStatus.Running or WorkTaskStatus.Completed)))
                throw new TaskGraphRepairRejectedException("owned-task-prerequisite", i,
                    "Insertion would add a dispatch-stage prerequisite to an owned task, even at a later position.");
            simulated.Insert(index, (role, $"new:{i}"));
            parsed.Add((role, item.Description.Trim(), before));
        }
        if (plan.ExpectedFinalRoleOrder is { } expected &&
            (expected.Any(value => !TryRole(value, out _)) ||
             !expected.Select(value => Enum.Parse<AgentRole>(value, true)).SequenceEqual(simulated.Select(entry => entry.RequiredRole))))
            throw new TaskGraphRepairRejectedException("final-order-mismatch", null,
                $"Actual order: {string.Join(" -> ", simulated.Select(entry => entry.RequiredRole))}.");

        foreach (var item in parsed)
            kernel.AddTask(goalId, item.Role, item.Description, beforeRole: item.Before);
        if (goal.Status == GoalStatus.Verified)
            goal.SetStatus(GoalStatus.Active);
    }

    private static bool TryRole(string? value, out AgentRole role) =>
        Enum.TryParse(value, true, out role) && Enum.IsDefined(role);
}
