using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static DeveloperResumedDeferredNoChangeQualifierTests;

public sealed class DeveloperDeferredNoChangeDeclineCodeTests
{
    private const string Unusable = "DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE candidate_sha=" +
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb; requested_classes=OtherTests";

    [Xunit.Theory]
    [Xunit.InlineData("not-developer")]
    [Xunit.InlineData("not-retry-or-resume")]
    [Xunit.InlineData("dirty")]
    [Xunit.InlineData("commit-after-dispatch")]
    [Xunit.InlineData("candidate-not-head")]
    [Xunit.InlineData("no-rationale")]
    [Xunit.InlineData("no-worker-result")]
    [Xunit.InlineData("blockers")]
    [Xunit.InlineData("tests-not-deferred")]
    [Xunit.InlineData("no-classes")]
    [Xunit.InlineData("failing-test-unresolvable")]
    [Xunit.InlineData("no-finding-classes")]
    [Xunit.InlineData("failing-test-class-undeclared")]
    [Xunit.InlineData("finding-class-undeclared")]
    public void EachDeclineCheckHasOneFixedCode(string expected)
    {
        var cause = expected is "no-finding-classes" or "finding-class-undeclared"
            ? RetryCause.NewTestFinding : RetryCause.Unknown;
        var message = expected switch
        {
            "failing-test-unresolvable" => "failing_tests=DisplayNameOnly",
            "failing-test-class-undeclared" => "failing_tests=Suite.UndeclaredTests.Fails",
            _ => "Review candidate again."
        };
        var (kernel, goal, task, _) = Scenario(message, cause,
            role: expected == "not-developer" ? AgentRole.Tester : AgentRole.Developer,
            retry: expected != "not-retry-or-resume");
        if (expected == "finding-class-undeclared") RecordFinding(kernel, goal, "UndeclaredTests");
        var output = expected switch
        {
            "no-rationale" => Output.Replace(Rationale + "\n", string.Empty, StringComparison.Ordinal),
            "no-worker-result" => Rationale,
            "blockers" => Output.Replace("blockers: none", "blockers: source work remains", StringComparison.Ordinal),
            "tests-not-deferred" => Output.Replace("deferred - ClassA", "pass - ClassA", StringComparison.Ordinal),
            "no-classes" => Output.Replace("deferred - ClassA", "deferred - conductor will verify", StringComparison.Ordinal),
            _ => Output
        };

        Xunit.Assert.False(QualifyWithCode(goal, task, output, out var code,
            clean: expected != "dirty", relevantCommit: expected == "commit-after-dispatch",
            head: expected == "candidate-not-head" ? new string('c', 40) : Candidate));
        Xunit.Assert.Equal(expected, code);
    }

    [Xunit.Theory]
    [Xunit.InlineData("quoted-marker")]
    [Xunit.InlineData("lowercase-marker")]
    [Xunit.InlineData("newer-retry")]
    public void OnlyNewestOrdinalPrefixCanRelaxMissingFindingClasses(string invalid)
    {
        var message = invalid switch
        {
            "quoted-marker" => "Retry after " + Unusable,
            "lowercase-marker" => Unusable.ToLowerInvariant(),
            _ => Unusable
        };
        var (kernel, goal, task, clock) = Scenario(message, RetryCause.NewTestFinding, dispatch: false);
        if (invalid == "newer-retry")
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            kernel.RetryTask(goal.Id, task.Id, "New evidence request without a marker.", RetryCause.NewTestFinding);
        }
        Dispatch(kernel, goal, task, clock.UtcNow);

        Xunit.Assert.False(QualifyWithCode(goal, task, Output, out var code));
        Xunit.Assert.Equal("no-finding-classes", code);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void UnusableRetryAcceptsAnyDeclaredClassesAndStillAcceptsDeclaredFinding(bool hasFinding)
    {
        var (kernel, goal, task, _) = Scenario("  " + Unusable, RetryCause.NewTestFinding);
        if (hasFinding) RecordFinding(kernel, goal, "ClassA");

        Xunit.Assert.True(QualifyWithCode(goal, task, Output, out var code));
        Xunit.Assert.Null(code);
    }

    [Xunit.Fact]
    public void UnusableRetryStillRequiresEveryFailingTestClass()
    {
        var (_, goal, task, _) = Scenario(Unusable + "; failing_tests=Suite.UndeclaredTests.Fails",
            RetryCause.NewTestFinding);

        Xunit.Assert.False(QualifyWithCode(goal, task, Output, out var code));
        Xunit.Assert.Equal("failing-test-class-undeclared", code);
    }

    [Xunit.Fact]
    public void FirstFailingCheckWinsWhenSeveralChecksFail()
    {
        var (_, goal, task, _) = Scenario("Retry the candidate.", RetryCause.Unknown);

        Xunit.Assert.False(QualifyWithCode(goal, task, string.Empty, out var code,
            clean: false, relevantCommit: true, head: new string('c', 40)));
        Xunit.Assert.Equal("dirty", code);
    }

    internal static void RecordFinding(AgentOrchestratorKernel kernel, Goal goal, string testClass)
    {
        var reviewer = kernel.AddTask(goal.Id, AgentRole.Reviewer, "Record a source finding.");
        var finding = new ReviewFinding("source-finding", ReviewFindingState.Open,
            new ReviewFindingLocation("seed.txt", "candidate"), "The selected source evidence is required.",
            FindingSeverity.Blocking, FindingCategory.Correctness,
            new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", testClass)]));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review fixture", "C:\\repo", 1, string.Empty, "Source finding remains.",
            DateTimeOffset.Parse("2026-10-02T09:01:00Z"), MergedReviewFindings: [finding]));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TestClock Clock) Scenario(
        string message, RetryCause cause, AgentRole role = AgentRole.Developer, bool retry = true, bool dispatch = true)
    {
        var clock = new TestClock { UtcNow = DateTimeOffset.Parse("2026-10-02T09:00:00Z") };
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Review the candidate.", role);
        var goal = kernel.CreateGoal("Deferred no-change decline codes", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        if (retry) kernel.RetryTask(goal.Id, task.Id, message, cause);
        if (dispatch) Dispatch(kernel, goal, task, clock.UtcNow);
        return (kernel, goal, task, clock);
    }

    private static void Dispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, DateTimeOffset time) =>
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", time, BaseCommit: Candidate));

    private static bool QualifyWithCode(Goal goal, TaskSpec task, string output, out string? code,
        bool clean = true, bool relevantCommit = false, string? head = null)
    {
        var catalog = WorkerProviderCatalog.Default();
        var classifier = new WorkerDispatchCompletionClassifier(
            dispatch => catalog.ResolveProfile(dispatch.WorkerName), new TestClock(),
            _ => false, _ => throw new FileNotFoundException());
        // Keep the added facts compilable against the original qualifier for Acceptance's negative control.
        var method = typeof(DeveloperDeferredNoChangeQualifier).GetMethods(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "TryQualify" && method.GetParameters().Length == 10);
        Xunit.Assert.NotNull(method);
        object?[] arguments = [goal, task, head ?? Candidate, clean, relevantCommit, output,
            string.Empty, classifier, null, null];
        var qualified = (bool)method.Invoke(null, arguments)!;
        code = (string?)arguments[9];
        return qualified;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
