using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static DeveloperResumedDeferredNoChangeQualifierTests;

public sealed class DeveloperDottedIdentityNoChangeQualifierTests
{
    [Xunit.Fact]
    public void FailingClassNamedByMethodIdentityQualifies()
    {
        var clock = new FixedClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Recheck the candidate.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Dotted identity deferral", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, task.Id,
            "ACTIONABLE_CANDIDATE_RED failing_tests=CanaryEngineSurfaceCoverageTests.Verifier_dependencies_are_covered_by_canary_surface",
            RetryCause.Unknown);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", clock.UtcNow, BaseCommit: Candidate));
        var catalog = WorkerProviderCatalog.Default();
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => catalog.ResolveProfile(dispatch.WorkerName), clock,
            _ => false, _ => throw new FileNotFoundException());
        var output = Output.Replace("deferred - ClassA", "deferred - AlphaTests, " +
            "CanaryEngineSurfaceCoverageTests.Verifier_dependencies_are_covered_by_canary_surface",
            StringComparison.Ordinal);

        Xunit.Assert.True(DeveloperDeferredNoChangeQualifier.TryQualify(goal, task, Candidate,
            true, false, output, string.Empty, classifier, out var outcome, out var code));
        Xunit.Assert.Null(code);
        Xunit.Assert.Equal(["AlphaTests", "CanaryEngineSurfaceCoverageTests"], outcome.TestClasses);

        Xunit.Assert.False(DeveloperDeferredNoChangeQualifier.TryQualify(goal, task, Candidate,
            true, false, Output.Replace("ClassA", "AlphaTests", StringComparison.Ordinal),
            string.Empty, classifier, out _, out code));
        Xunit.Assert.Equal("failing-test-class-undeclared", code);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    }
}
