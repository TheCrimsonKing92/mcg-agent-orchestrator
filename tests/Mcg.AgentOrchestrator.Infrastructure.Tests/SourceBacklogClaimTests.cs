using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class SourceBacklogClaimTests
{
    [Xunit.Theory]
    [Xunit.InlineData("failed", GoalStatus.Failed, GoalLifecycleState.Failed)]
    [Xunit.InlineData("cancelled", GoalStatus.Cancelled, GoalLifecycleState.Failed)]
    [Xunit.InlineData("superseded", GoalStatus.Superseded, GoalLifecycleState.Failed)]
    [Xunit.InlineData("merged", GoalStatus.Completed, GoalLifecycleState.Merged)]
    [Xunit.InlineData("recorded", GoalStatus.Completed, GoalLifecycleState.Recorded)]
    [Xunit.InlineData("cleaned-up", GoalStatus.Completed, GoalLifecycleState.CleanedUp)]
    public void SiblingAdmissionAllowsEachTerminalOwnerShape(
        string terminalShape,
        GoalStatus expectedStatus,
        GoalLifecycleState expectedLifecycleState)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Establish terminal owner state", AgentRole.Developer);
        var owner = kernel.CreateGoal($"Terminal {terminalShape} source owner", [task]);

        switch (terminalShape)
        {
            case "failed":
                kernel.EscalateTaskFailure(owner.Id, task.Id, "Terminal failure fixture.");
                break;
            case "cancelled":
                kernel.CancelGoal(owner.Id, "Terminal cancellation fixture.");
                break;
            case "superseded":
                kernel.SupersedeGoal(owner.Id, "Terminal supersession fixture.");
                break;
            case "merged":
            case "recorded":
            case "cleaned-up":
                kernel.ActivateGoal(owner.Id, AgentCatalog.Default().Agents);
                kernel.RecordTaskDispatch(
                    owner.Id,
                    task.Id,
                    new TaskDispatchRecord("worker", "codex exec", root, DateTimeOffset.UtcNow));
                kernel.RecordDispatchExecutionResult(
                    owner.Id,
                    task.Id,
                    new TaskVerificationRecord(
                        "codex exec",
                        root,
                        0,
                        "WORKER_RESULT: files: none commands: none tests: pass commit: none blockers: none model_fit: test skills: none confidence: high END_WORKER_RESULT",
                        "",
                        DateTimeOffset.UtcNow,
                        WorkerResultPresent: true));
                kernel.CompleteGoal(owner.Id, "Completed source owner fixture.");
                GoalOperationJournal.Completed(root, owner, "conductor:land", "Landed fixture.");
                if (terminalShape is "recorded" or "cleaned-up")
                    GoalOperationJournal.Completed(root, owner, "conductor:record", "Recorded fixture.");
                if (terminalShape == "cleaned-up")
                    GoalOperationJournal.Completed(root, owner, "workspace:remove", "Cleaned fixture.");
                break;
        }

        var facts = CliCommandHandlers.BuildSourceBacklogSiblingFacts(
            root,
            owner,
            SourceBacklogCoverage.Full,
            workspaceExists: false);

        Xunit.Assert.Equal(expectedStatus, facts.OwnerStatus);
        Xunit.Assert.Equal(expectedLifecycleState, facts.OwnerLifecycleState);
        Xunit.Assert.Equal(SourceBacklogSiblingAdmission.Admit, SourceBacklogSiblingAdmissionPolicy.Evaluate(facts));
    }

    [Xunit.Fact]
    public void SiblingAdmissionAllowsActiveSliceOwner()
    {
        var facts = new SourceBacklogSiblingFacts(
            SourceBacklogCoverage.Slice,
            GoalStatus.Active,
            GoalLifecycleState.Created);

        Xunit.Assert.Equal(SourceBacklogSiblingAdmission.Admit, SourceBacklogSiblingAdmissionPolicy.Evaluate(facts));
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Active, GoalLifecycleState.Created)]
    [Xunit.InlineData(GoalStatus.Completed, GoalLifecycleState.Verified)]
    public void SiblingAdmissionRefusesNonTerminalFullOwner(
        GoalStatus ownerStatus,
        GoalLifecycleState ownerLifecycleState)
    {
        var facts = new SourceBacklogSiblingFacts(
            SourceBacklogCoverage.Full,
            ownerStatus,
            ownerLifecycleState);

        Xunit.Assert.Equal(
            SourceBacklogSiblingAdmission.RefuseActiveOwnerFullCoverage,
            SourceBacklogSiblingAdmissionPolicy.Evaluate(facts));
        Xunit.Assert.Equal(
            "source-backlog-active-owner-full-coverage",
            SourceBacklogSiblingAdmissionPolicy.ActiveOwnerFullCoverageReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Cancelled, false, GoalReplacementDisposition.ZeroWorkCorrection, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Cancelled, true, GoalReplacementDisposition.ZeroWorkCorrection, GoalReplacementOutcome.IneligibleDisposition)]
    [Xunit.InlineData(GoalStatus.Cancelled, true, GoalReplacementDisposition.SupersedeUnlandedAttempt, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Failed, true, GoalReplacementDisposition.AbandonFailedAttempt, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Superseded, true, GoalReplacementDisposition.SupersedeUnlandedAttempt, GoalReplacementOutcome.Succeeded)]
    [Xunit.InlineData(GoalStatus.Cancelled, false, GoalReplacementDisposition.Unspecified, GoalReplacementOutcome.IneligibleDisposition)]
    [Xunit.InlineData(GoalStatus.Active, false, GoalReplacementDisposition.ZeroWorkCorrection, GoalReplacementOutcome.ProtectedOwner)]
    public void EligibilityUsesExplicitDispositionMatrix(
        GoalStatus status,
        bool hasWork,
        GoalReplacementDisposition disposition,
        GoalReplacementOutcome expected)
    {
        var facts = new GoalReplacementEligibilityFacts(
            status,
            HasWorkspace: hasWork,
            HasBranch: false,
            HasDispatch: false,
            HasRepositoryDelta: false,
            IsRunning: false,
            IsLanded: false,
            IsMerged: false,
            IsRecorded: false);

        Xunit.Assert.Equal(expected, SourceBacklogClaimEligibility.Evaluate(disposition, facts));
    }

    [Xunit.Fact]
    public async Task LegacyMultipleLinksFailClosedWithoutInventingOwner()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var first = kernel.CreateGoal("First historical source association");
        var second = kernel.CreateGoal("Second historical source association");
        const string backlogItemId = "legacy-ambiguous-source";
        kernel.SetGoalSourceBacklogItemLink(first.Id, backlogItemId, SourceBacklogCoverage.Full);
        kernel.SetGoalSourceBacklogItemLink(second.Id, backlogItemId, SourceBacklogCoverage.Full);
        await repository.SaveAsync(kernel);

        var exception = Xunit.Assert.Throws<LegacySourceBacklogOwnerAmbiguousException>(() =>
            new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(kernel, backlogItemId));

        Xunit.Assert.Equal(backlogItemId, exception.BacklogItemId);
        Xunit.Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(), exception.LinkedGoalIds.Order());
    }

    [Xunit.Fact]
    public void OrdinaryClaimInitializationRequiresAmbientStateTransaction()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Ordinary source-linked goal");
        const string backlogItemId = "ordinary-standalone-claim";
        kernel.SetGoalSourceBacklogItemLink(goal.Id, backlogItemId, SourceBacklogCoverage.Slice);
        var store = new SourceBacklogClaimStore(workspace.SqliteStatePath);

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            store.EnsureClaimForNewGoal(
                kernel,
                backlogItemId,
                goal.Id.Value,
                SourceBacklogCoverage.Slice));

        Xunit.Assert.Contains("must join the active state transaction", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Null(store.ResolveClaim(new AgentOrchestratorKernel(), backlogItemId));
    }

    [Xunit.Fact]
    public void ProtectedLandingEvidenceOverridesOtherwiseEligibleTerminalStatus()
    {
        var facts = new GoalReplacementEligibilityFacts(
            GoalStatus.Cancelled,
            HasWorkspace: false,
            HasBranch: false,
            HasDispatch: false,
            HasRepositoryDelta: false,
            IsRunning: false,
            IsLanded: true,
            IsMerged: false,
            IsRecorded: false);

        Xunit.Assert.Equal(
            GoalReplacementOutcome.ProtectedOwner,
            SourceBacklogClaimEligibility.Evaluate(GoalReplacementDisposition.ZeroWorkCorrection, facts));
    }

    [Xunit.Fact]
    public void UnavailableGitEvidenceFailsClosed()
    {
        var facts = new GoalReplacementEligibilityFacts(
            GoalStatus.Cancelled,
            HasWorkspace: false,
            HasBranch: false,
            HasDispatch: false,
            HasRepositoryDelta: false,
            IsRunning: false,
            IsLanded: false,
            IsMerged: false,
            IsRecorded: false,
            IsGitEvidenceAvailable: false);

        Xunit.Assert.Equal(
            GoalReplacementOutcome.ProtectedOwner,
            SourceBacklogClaimEligibility.Evaluate(GoalReplacementDisposition.ZeroWorkCorrection, facts));
    }
}
