using Mcg.AgentOrchestrator.Core;

public sealed class ParallelExecutionPlannerTests
{
    [Xunit.Fact(DisplayName = "ParallelExecutionPlanner_batches_independent_file_touching_goals")]
    public void ParallelExecutionPlannerBatchesIndependentFileTouchingGoals()
{
    var plan = ParallelExecutionPlanner.Build(
    [
        new ParallelExecutionIntent("goal-a-dev", "goal-a", ["src/A.cs"], ProviderKey: "codex"),
        new ParallelExecutionIntent("goal-b-dev", "goal-b", ["src/B.cs"], ProviderKey: "codex")
    ],
    [
        new ParallelExecutionProviderQuota("codex", 2)
    ]);

    Assert.Equal(1, plan.Batches.Count);
    Assert.Equal(2, plan.Batches[0].IntentIds.Count);
    foreach (var decision in plan.Decisions)
    {
        Assert.Equal(ParallelExecutionDisposition.Concurrent, decision.Disposition);
    }
}

    [Xunit.Fact(DisplayName = "ParallelExecutionPlanner_serializes_conflicting_paths_and_shared_worktrees")]
    public void ParallelExecutionPlannerSerializesConflictingPathsAndSharedWorktrees()
{
    var plan = ParallelExecutionPlanner.Build(
    [
        new ParallelExecutionIntent("edit-parent", "goal-a", ["src/Feature"], ProviderKey: "codex"),
        new ParallelExecutionIntent("edit-child", "goal-b", ["src/Feature/File.cs"], ProviderKey: "claude"),
        new ParallelExecutionIntent("same-goal-test", "goal-a", ["tests/FeatureTests.cs"], ProviderKey: "codex")
    ]);

    Assert.Equal(2, plan.Batches.Count);
    Assert.Equal(ParallelExecutionDisposition.Concurrent, Decision(plan, "edit-parent").Disposition);
    Assert.Equal(ParallelExecutionDisposition.Serialized, Decision(plan, "edit-child").Disposition);
    Assert.Equal(ParallelExecutionDisposition.Serialized, Decision(plan, "same-goal-test").Disposition);
}

    [Xunit.Fact(DisplayName = "ParallelExecutionPlanner_serializes_state_lifecycle_acceptance_and_resource_conflicts")]
    public void ParallelExecutionPlannerSerializesStateLifecycleAcceptanceAndResourceConflicts()
{
    var plan = ParallelExecutionPlanner.Build(
    [
        new ParallelExecutionIntent("dispatch", "goal-a", RequiredResources: ["state"], WritesGoalState: true),
        new ParallelExecutionIntent("workspace-remove", "goal-b", MutatesWorkspaceLifecycle: true),
        new ParallelExecutionIntent("acceptance", "goal-c", RunsAcceptance: true),
        new ParallelExecutionIntent("same-resource", "goal-d", RequiredResources: ["state"])
    ]);

    Assert.Equal(4, plan.Batches.Count);
    foreach (var decision in plan.Decisions)
    {
        Assert.True(decision.BatchNumber is not null);
    }
    Assert.Equal(ParallelExecutionDisposition.Serialized, Decision(plan, "workspace-remove").Disposition);
    Assert.Equal(ParallelExecutionDisposition.Serialized, Decision(plan, "acceptance").Disposition);
    Assert.Equal(ParallelExecutionDisposition.Serialized, Decision(plan, "same-resource").Disposition);
}

    [Xunit.Fact(DisplayName = "ParallelExecutionPlanner_honors_provider_quotas_dependencies_and_approval_gates")]
    public void ParallelExecutionPlannerHonorsProviderQuotasDependenciesAndApprovalGates()
{
    var plan = ParallelExecutionPlanner.Build(
    [
        new ParallelExecutionIntent("first", "goal-a", ["src/A.cs"], ProviderKey: "codex"),
        new ParallelExecutionIntent("second", "goal-b", ["src/B.cs"], ProviderKey: "codex"),
        new ParallelExecutionIntent("after-first", "goal-c", ["src/C.cs"], DependsOn: ["first"]),
        new ParallelExecutionIntent("needs-approval", "goal-d", ["src/D.cs"], RequiresOperatorApproval: true)
    ],
    [
        new ParallelExecutionProviderQuota("codex", 1)
    ]);

    Assert.Equal(2, plan.Batches.Count);
    Assert.Equal(1, plan.Batches[0].IntentIds.Count);
    Assert.Equal(ParallelExecutionDisposition.Serialized, Decision(plan, "second").Disposition);
    Assert.True(Decision(plan, "after-first").BatchNumber > Decision(plan, "first").BatchNumber);
    Assert.Equal(ParallelExecutionDisposition.RequiresOperatorApproval, Decision(plan, "needs-approval").Disposition);
    Assert.True(Decision(plan, "needs-approval").BatchNumber is null);
}

    [Xunit.Fact(DisplayName = "ParallelExecutionPlanner_requires_approval_for_high_risk_and_generated_write_sets")]
    public void ParallelExecutionPlannerRequiresApprovalForHighRiskAndGeneratedWriteSets()
{
    var plan = ParallelExecutionPlanner.Build(
    [
        new ParallelExecutionIntent("core-change", "goal-a", ["src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.cs"]),
        new ParallelExecutionIntent("generated-change", "goal-b", ["src/Mcg.AgentOrchestrator.App/bin/Debug/generated.dll"])
    ]);

    Assert.Empty(plan.Batches);
    Assert.Equal(ParallelExecutionDisposition.RequiresOperatorApproval, Decision(plan, "core-change").Disposition);
    Assert.Contains("high-risk ownership area", Decision(plan, "core-change").Reasons, StringComparison.Ordinal);
    Assert.Equal(ParallelExecutionDisposition.RequiresOperatorApproval, Decision(plan, "generated-change").Disposition);
    Assert.Contains("generated/noisy path", Decision(plan, "generated-change").Reasons, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "ParallelExecutionPlanner_serializes_shared_ownership_reservations")]
    public void ParallelExecutionPlannerSerializesSharedOwnershipReservations()
{
    var plan = ParallelExecutionPlanner.Build(
    [
        new ParallelExecutionIntent("dashboard-rendering", "goal-a", ["src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.cs"]),
        new ParallelExecutionIntent("dashboard-assets", "goal-b", ["src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardAssets.cs"]),
        new ParallelExecutionIntent("docs", "goal-c", ["docs/operator.md"])
    ]);

    Assert.Equal(2, plan.Batches.Count);
    Assert.Equal(ParallelExecutionDisposition.Concurrent, Decision(plan, "dashboard-rendering").Disposition);
    Assert.Equal(ParallelExecutionDisposition.Serialized, Decision(plan, "dashboard-assets").Disposition);
    Assert.Equal(ParallelExecutionDisposition.Concurrent, Decision(plan, "docs").Disposition);
}

    [Xunit.Fact(DisplayName = "ParallelExecutionPlanner_auto_approves_high_risk_ownership_when_policy_opts_in")]
    public void ParallelExecutionPlannerAutoApprovesHighRiskOwnershipWhenPolicyOptsIn()
{
    var plan = ParallelExecutionPlanner.Build(
    [
        new ParallelExecutionIntent("script-change", "goal-a", ["scripts/Invoke-TestSummary.ps1"], ProviderKey: "codex"),
        new ParallelExecutionIntent("generated-change", "goal-b", ["src/Mcg.AgentOrchestrator.App/bin/Debug/generated.dll"], ProviderKey: "claude")
    ],
    approveHighRiskOwnership: true);

    // A purely high-risk ownership write-set (a script) is auto-approved and batched, with an audit reason.
    Assert.Equal(ParallelExecutionDisposition.Concurrent, Decision(plan, "script-change").Disposition);
    Assert.Contains(
        Decision(plan, "script-change").Reasons,
        reason => reason.Contains("auto-approved by autonomy policy", StringComparison.Ordinal));
    // Generated/noisy paths still require operator cleanup even when ownership auto-approval is on.
    Assert.Equal(ParallelExecutionDisposition.RequiresOperatorApproval, Decision(plan, "generated-change").Disposition);
    Assert.True(Decision(plan, "generated-change").BatchNumber is null);
}

    private static ParallelExecutionDecision Decision(ParallelExecutionPlan plan, string intentId)
{
    return plan.Decisions.Single(decision => decision.IntentId == intentId);
}
}
