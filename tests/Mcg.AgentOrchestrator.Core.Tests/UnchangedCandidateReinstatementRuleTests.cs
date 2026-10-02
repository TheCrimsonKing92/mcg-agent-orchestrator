using Mcg.AgentOrchestrator.Core;

// Parallel safe: in-memory kernel state, injected clock, no I/O.
public sealed class UnchangedCandidateReinstatementRuleTests
{
    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void InheritedResetReusesOriginalVerificationWithoutAddingHistory(AgentRole role)
    {
        var (kernel, goal, task, identity) = InheritedReset(role);
        var prior = Assert.Single(task.VerificationHistory);
        var eventCount = goal.Timeline.Count;

        var result = kernel.ReinstateUnchangedCandidateVerdict(goal.Id, task.Id, identity);

        Assert.NotNull(result);
        Assert.Same(prior, result.PriorVerification);
        Assert.Same(prior, task.LastVerification);
        Assert.Single(task.VerificationHistory);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.False(task.LatestRetryInherited);
        Assert.Null(task.LastDispatch);
        var evt = Assert.Single(goal.Timeline.Skip(eventCount));
        Assert.Equal(ProgressKind.TaskUpdated, evt.Kind);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Equal(result.Render(), evt.Message);
        Assert.Null(kernel.ReinstateUnchangedCandidateVerdict(goal.Id, task.Id, identity));
        Assert.Equal(eventCount + 1, goal.Timeline.Count);
    }

    [Fact]
    public void DirectRetryAfterInheritedResetCannotReinstate()
    {
        var (kernel, goal, task, identity) = InheritedReset();
        kernel.RetryTask(goal.Id, task.Id, "Repeat without new input.", RetryCause.UnchangedContextRepeat);

        Assert.False(task.LatestRetryInherited);
        Assert.NotNull(UnchangedCandidateRule.Evaluate(goal, task, identity));
        Assert.Null(kernel.ReinstateUnchangedCandidateVerdict(goal.Id, task.Id, identity));
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Ideation)]
    public void OtherRolesRemainIneligible(AgentRole role)
    {
        var (_, goal, task, identity) = InheritedReset(role);

        Assert.NotNull(UnchangedCandidateRule.Evaluate(goal, task, identity));
        Assert.Null(UnchangedCandidateRule.EvaluateReinstatement(goal, task, identity));
    }

    [Theory]
    [InlineData("patch-changed", "base", "manifest")]
    [InlineData("patch", "base-changed", "manifest")]
    [InlineData("patch", "base", "manifest-changed")]
    public void AnyChangedCandidateComponentPreventsReinstatement(string patch, string basis, string manifest)
    {
        var (_, goal, task, _) = InheritedReset();

        Assert.Null(UnchangedCandidateRule.EvaluateReinstatement(goal, task,
            new CandidateIdentity(patch, basis, manifest)));
        Assert.Null(UnchangedCandidateRule.EvaluateReinstatement(goal, task, null));
    }

    [Fact]
    public void AcceptedFeedbackIsNewInputAndPreventsReinstatement()
    {
        var (_, goal, task, identity) = InheritedReset();
        task.RecordAcceptedRetryFeedback("Read the new finding.", task.VerificationHistory.Single().CompletedAt.AddSeconds(1));

        Assert.Null(UnchangedCandidateRule.Evaluate(goal, task, identity));
        Assert.Null(UnchangedCandidateRule.EvaluateReinstatement(goal, task, identity));
    }

    [Fact]
    public void PriorVerdictFromAnotherTaskCannotBeReinstated()
    {
        var (kernel, goal, task, identity) = InheritedReset();
        var other = kernel.AddTask(goal.Id, AgentRole.Tester, "Another test task");
        other.RecordRetry(task.LatestRetryAt!.Value, RetryCause.UnchangedContextRepeat, inherited: true);

        Assert.NotNull(UnchangedCandidateRule.Evaluate(goal, other, identity));
        Assert.Null(UnchangedCandidateRule.EvaluateReinstatement(goal, other, identity));
    }

    [Fact]
    public void ResetProvenanceAndCriteriaStampSurviveSnapshotRoundTrip()
    {
        var (kernel, goal, task, identity) = InheritedReset();
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredGoal = restoredKernel.GetGoal(goal.Id);
        var restoredTask = restoredGoal.FindTask(task.Id);

        Assert.True(restoredTask.LatestRetryInherited);
        Assert.Equal(task.LatestRetryAt, restoredTask.LatestRetryAt);
        Assert.Equal(task.VerificationHistory.Single().AcceptanceCriteriaVersionHash,
            restoredTask.VerificationHistory.Single().AcceptanceCriteriaVersionHash);
        Assert.NotNull(restoredKernel.ReinstateUnchangedCandidateVerdict(goal.Id, task.Id, identity));
        var completed = AgentOrchestratorKernel.FromSnapshot(restoredKernel.ExportSnapshot())
            .GetGoal(goal.Id).FindTask(task.Id);
        Assert.False(completed.LatestRetryInherited);
        Assert.Equal(WorkTaskStatus.Completed, completed.Status);
        Assert.Same(Assert.Single(completed.VerificationHistory), completed.LastVerification);
        Assert.Equal(restoredTask.LastVerification!.AcceptanceCriteriaVersionHash,
            completed.LastVerification!.AcceptanceCriteriaVersionHash);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LegacyMissingProvenanceOrCriteriaStampKeepsHold(bool removeProvenance, bool removeStamp)
    {
        var (kernel, goal, task, identity) = InheritedReset();
        var snapshot = kernel.ExportSnapshot();
        var savedGoal = snapshot.Goals.Single();
        var savedTask = savedGoal.Tasks.Single();
        var legacy = savedTask with
        {
            LatestRetryInherited = !removeProvenance,
            VerificationHistory = savedTask.VerificationHistory!.Select(record => record with
            {
                AcceptanceCriteriaVersionHash = removeStamp ? null : record.AcceptanceCriteriaVersionHash
            }).ToArray()
        };
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [savedGoal with { Tasks = [legacy] }]
        });
        var restoredGoal = restored.GetGoal(goal.Id);
        var restoredTask = restoredGoal.FindTask(task.Id);

        Assert.NotNull(UnchangedCandidateRule.Evaluate(restoredGoal, restoredTask, identity));
        Assert.Null(restored.ReinstateUnchangedCandidateVerdict(goal.Id, task.Id, identity));
        Assert.Equal(WorkTaskStatus.Assigned, restoredTask.Status);
    }

    [Fact]
    public void SameRoundEnrichmentKeepsCriteriaStamp()
    {
        var (_, _, task, _) = InheritedReset();
        var verification = task.VerificationHistory.Single();
        var unStamped = verification with { AcceptanceCriteriaVersionHash = null };

        Assert.Equal(verification.AcceptanceCriteriaVersionHash,
            verification.MergeSameRoundEnrichment(unStamped).AcceptanceCriteriaVersionHash);
        Assert.Equal(verification.AcceptanceCriteriaVersionHash,
            unStamped.MergeSameRoundEnrichment(verification).AcceptanceCriteriaVersionHash);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, CandidateIdentity Identity)
        InheritedReset(AgentRole role = AgentRole.Tester)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Verify candidate", role);
        var goal = kernel.CreateGoal("Prior verdict", [task]);
        // Ideation is not in the usual test catalog; declare the assigned agent explicitly.
        var agents = DefaultAgents().ToList();
        if (!agents.Any(agent => agent.Role == role))
            agents.Add(new AgentDefinition(AgentId.New(), "fixture", role, agents[0].Model));
        kernel.ActivateGoal(goal.Id, agents);
        var identity = new CandidateIdentity("patch", "base", "manifest");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "fixture", "worker", @"C:\repo", clock.UtcNow, CandidateIdentity: identity));
        clock.Advance();
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "worker", @"C:\repo", 0,
            role == AgentRole.Reviewer
                ? "WORKER_RESULT:\nblockers: none\nfindings: []\ntouched_anchors: []\nverdict: pass\nEND_WORKER_RESULT"
                : "ok", "", clock.UtcNow, WorkerResultPresent: true, CandidateIdentity: identity));
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        clock.Advance();
        kernel.RetryTask(goal.Id, task.Id, "Reset for test setup.", RetryCause.UnchangedContextRepeat);
        // Unit-level fixture for the provenance written by ResetTaskForRetry(inherited: true).
        task.RecordRetry(clock.UtcNow, RetryCause.UnchangedContextRepeat, inherited: true);
        Assert.True(task.LatestRetryInherited);
        return (kernel, goal, task, identity);
    }
}
