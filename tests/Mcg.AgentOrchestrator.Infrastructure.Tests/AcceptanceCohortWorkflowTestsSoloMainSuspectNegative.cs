using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every theory case owns its repository, databases and circuit identity.
public sealed class AcceptanceCohortWorkflowTestsSoloMainSuspectNegative : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData("different-main")]
    [InlineData("disjoint")]
    [InlineData("touched-source")]
    [InlineData("touched-earlier-source")]
    [InlineData("unconfirmed")]
    [InlineData("merge-base-mismatch")]
    [InlineData("missing-baseline-evidence")]
    [InlineData("same-candidate")]
    [InlineData("unresolved-source")]
    public async Task SecondSoloGate_WithoutCorroboration_LeavesCircuitHealthy(string kind)
    {
        using var scenario = AcceptanceCohortWorkflowTestsSoloMainSuspect.CreateScenario(
            firstTouchesSource: kind == "touched-earlier-source", secondTouchesSource: kind == "touched-source");
        var shared = kind == "unresolved-source" ? "Fixture.MissingMainRedTests.Fails" :
            AcceptanceCohortWorkflowTestsSoloMainSuspect.TestA;
        scenario.Complete(0, scenario.Summary(0, [shared]));
        Assert.Single(scenario.ReadReceipts());
        var main = scenario.MainSha;
        if (kind == "different-main")
        {
            File.WriteAllText(Path.Combine(scenario.Repo, "advance.txt"), "advanced main");
            RunGit(scenario.Repo, "add", "advance.txt");
            RunGit(scenario.Repo, "commit", "-m", "Advance observed main");
            main = RunGitOutput(scenario.Repo, "rev-parse", "main").Trim();
            Assert.NotEqual(scenario.MainSha, main);
        }
        var second = scenario.Summary(1,
            [kind == "disjoint" ? AcceptanceCohortWorkflowTestsSoloMainSuspect.TestB : shared],
            mainSha: main, baselineSha: kind == "merge-base-mismatch" ? "another-baseline" : main,
            unconfirmed: kind == "unconfirmed");
        if (kind == "same-candidate")
            second = second with { BranchHeadSha = scenario.Candidates[0].BranchHeadSha };
        if (kind == "missing-baseline-evidence")
        {
            var check = second.UnmetCriteria.Single();
            second = new AcceptanceVerificationSummary(false, [check with
            {
                FailingTestAttributions = [new(shared, AcceptanceTestFailureOrigin.Inherited, "merge-base attribution")]
            }], FailedChecks: second.FailedChecks, BranchHeadSha: second.BranchHeadSha, MainHeadSha: second.MainHeadSha);
        }

        scenario.Complete(1, second);

        Assert.Empty(await scenario.Events.ReadAllAsync());
        Assert.Empty(scenario.SoloLines());
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        if (kind is "unconfirmed" or "merge-base-mismatch" or "missing-baseline-evidence")
            Assert.Single(scenario.ReadReceipts());
    }

    [Fact]
    public async Task SameSoloGoal_NewCandidate_DoesNotCorroborateItself()
    {
        using var scenario = AcceptanceCohortWorkflowTestsSoloMainSuspect.CreateScenario();
        scenario.Complete(0, scenario.Summary(0, [AcceptanceCohortWorkflowTestsSoloMainSuspect.TestA]));
        var summary = scenario.Summary(0, [AcceptanceCohortWorkflowTestsSoloMainSuspect.TestA])
            with { BranchHeadSha = scenario.Candidates[1].BranchHeadSha };
        scenario.Complete(0, summary);

        Assert.Equal(2, scenario.ReadReceipts().Count);
        Assert.Empty(await scenario.Events.ReadAllAsync());
        Assert.Empty(scenario.SoloLines());
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
    }
}
