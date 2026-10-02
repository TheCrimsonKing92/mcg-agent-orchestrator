using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: each fact owns its temporary source tree and index, and uses a fixed clock.
public sealed class ConductorDriverTestsApparatusRedFailureLine
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string Project = "tests/Sample.Tests/Sample.Tests.csproj";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    [Fact]
    public void PassingRerun_HoldsWithFailureLine()
    {
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance("candidate failure"));

        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, held.Reason, StringComparison.Ordinal);
        Assert.Contains(Identity + ": candidate failure", held.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MultilineFailure_HoldsWithTruncatedFirstLineOnly()
    {
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            Advance(new string('A', 300) + "\r\nSECOND-LINE-MARKER\nthird"));

        Assert.Contains(Identity + ": " + new string('A', 200) + "...", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('A', 201), held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("SECOND-LINE-MARKER", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("third", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', held.Reason);
        Assert.DoesNotContain('\n', held.Reason);
    }

    [Fact]
    public void RegateCapExhausted_EscalatesWithFailureLine()
    {
        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(
            Advance("candidate failure", exhaustCap: true));

        Assert.Contains(ApparatusRedClassifier.BoundExhaustedToken, escalated.Reason, StringComparison.Ordinal);
        Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, escalated.Reason, StringComparison.Ordinal);
        Assert.Contains(Identity + ": candidate failure", escalated.Reason, StringComparison.Ordinal);
    }

    private static ConductorAdvanceOutcome Advance(string outputTail, bool exhaustCap = false)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var directory = Path.Combine(root, "tests", "Sample.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "UnchangedTests.cs"),
                "namespace Sample.Tests; public sealed class UnchangedTests { }");
            var index = new AcceptanceFailingTestIndex(
                Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            if (exhaustCap)
            {
                index.Append([new AcceptanceFailingTestIndexRecord(
                    AcceptanceFailingTestIndex.ContractVersion,
                    AcceptanceFailingTestIndexKinds.ApparatusRegate,
                    goal.Id.Value, Now.AddMinutes(-1))], Now);
            }

            var failed = FailingCheck(outputTail);
            Assert.Null(failed.TestResultPaths);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(failed),
                retryTaskWithCause: (_, _, _, _, _) =>
                    throw new InvalidOperationException("A passed candidate rerun must not reopen a worker."),
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Changed.cs"],
                apparatusRedGate: new ApparatusRedGate(index, _ => root, () => Now, perGoalRegateCap: 1));

            var outcome = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome;

            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            var occurrence = Assert.Single(index.Read().Where(record =>
                record.Kind == AcceptanceFailingTestIndexKinds.GateFailure));
            Assert.Equal(Identity, occurrence.TestIdentity);
            Assert.Equal(ApparatusRedClassifier.CandidateRerunEvidenceKind, occurrence.EvidenceKind);
            Assert.Equal(AcceptanceFailingTestIndex.ComputeMessageFingerprint(outputTail), occurrence.MessageFingerprint);
            return outcome;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceCheckResult FailingCheck(string outputTail) => new(
        Identity, Passed: false, ExitCode: 1, OutputTail: outputTail,
        FailingTestIdentities: [Identity], TestProjectPath: Project,
        FailingTestAttributions: [new AcceptanceTestFailureAttribution(
            Identity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced,
            "focused identity was green at merge-base baseline",
            new CandidateFailureRerunEvidence("Passed", "candidate-rerun.trx"))]);

    private static AcceptanceVerificationSummary RedSummary(AcceptanceCheckResult check) => new(
        false, [check], FailedChecks: [check.Name], BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
        CheckAttributions: [new AcceptanceCheckAttribution(
            check.Name, AcceptanceFailureOrigin.Introduced, "main green")]);
}
