using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

internal static class ResearcherGoalPlanFixture
{
    public static IReadOnlyList<TaskSpec> Tasks() =>
    [
        new(TaskId.New(), "Inspect current source and prior evidence before implementation.", AgentRole.Researcher,
            "Research notes must cite exact files or state that no source change is needed."),
        new(TaskId.New(), "Synthesize the durable research into a criterion-mapped implementation plan.", AgentRole.Planner,
            "Plan must map every acceptance criterion and identify target files, ownership, edge contracts, risks, and verification."),
        new(TaskId.New(), "Implement the scoped slice and keep changes narrow.", AgentRole.Developer,
            "Run focused automated tests or explain why a broader suite is required."),
        new(TaskId.New(), "Run focused verification and capture failures as evidence.", AgentRole.Tester,
            "Run focused automated tests or explain why a broader suite is required."),
        new(TaskId.New(), "Review diff, tests, and worker evidence before acceptance.", AgentRole.Reviewer,
            "Review must mention residual risk and acceptance readiness.")
    ];

    public static Goal Create(AgentOrchestratorKernel kernel, string objective)
    {
        var goal = kernel.CreateGoal(objective, Tasks());
        kernel.RecordGoalPolicyDecision(goal.Id,
            "Intake pipeline decision (override): five-role; reasons: operator override selected five-role; risk labels: low-risk.");
        return goal;
    }

    public static Goal CreateAndActivate(
        AgentOrchestratorKernel kernel, IReadOnlyList<AgentDefinition> agents, string objective)
    {
        var goal = Create(kernel, objective);
        kernel.ActivateGoal(goal.Id, agents);
        return goal;
    }

    public static GoalObjectivePlan RequestedPlan(string objective)
    {
        var plan = GoalObjectivePlanner.Build(objective);
        return plan with
        {
            PipelineDecision = plan.PipelineDecision with { IsOverride = true },
            TaskBoundaries = Tasks().Select((task, index) => new GoalObjectiveTaskBoundary(
                index + 1, task.RequiredRole, task.Description,
                task.RequiredRole is AgentRole.Developer or AgentRole.Tester ? "workspace-write" : "read-only",
                task.VerificationPlan!)).ToArray()
        };
    }
}
