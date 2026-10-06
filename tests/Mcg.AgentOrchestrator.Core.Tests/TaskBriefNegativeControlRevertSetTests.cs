using Mcg.AgentOrchestrator.Core;

public sealed class TaskBriefNegativeControlRevertSetTests
{
    private const string Declaration = "negative-control-revert: tests/A/Support.cs, tests/A/Counter.cs";
    private const string Candidate = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Developer)]
    public void BuildTaskBrief_ExposesTesterReceiptToEvidenceRolesOnly(AgentRole role)
    {
        var (kernel, goal) = CreateGoal(Declaration);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var request = new FindingEvidenceRequest([new("Core.Tests", "SupportProbeTests")],
            FindingEvidenceNegativeControl.RevertSrc, ["tests/A/Support.cs"]);
        var receipt = new FindingEvidenceReceipt("declared-receipt", Candidate, request, true, true, "demonstrated",
            Arms: [new(FindingEvidenceArm.SourceReverted, Candidate, FindingEvidenceArmDisposition.Red, true, false, "red",
                RestoredPaths: ["tests/A/Support.cs"], DroppedPaths: ["tests/A/Counter.cs"])],
            NegativeControlOutcome: FindingEvidenceNegativeControlOutcome.Demonstrated);
        kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord("receipt", "C:\\repo", 0,
            "", "", DateTimeOffset.UtcNow, FindingEvidenceReceipts: [receipt]));
        var brief = kernel.BuildTaskBrief(goal.Id, goal.Tasks.Single(task => task.RequiredRole == role).Id).Content;
        if (role == AgentRole.Developer)
        {
            Assert.DoesNotContain("## Negative-control revert set", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("declared: tests/A/Counter.cs, tests/A/Support.cs", brief, StringComparison.Ordinal);
            Assert.DoesNotContain("revert_set:", brief, StringComparison.Ordinal);
            return;
        }
        Assert.Contains("## Negative-control revert set", brief, StringComparison.Ordinal);
        Assert.Contains("declared: tests/A/Counter.cs, tests/A/Support.cs", brief, StringComparison.Ordinal);
        Assert.Contains($"revert_set: receipt=declared-receipt; candidate_sha={Candidate}; selection=Core.Tests:SupportProbeTests; restored=tests/A/Support.cs; dropped=tests/A/Counter.cs; mutation=none; outcome=negative-control-demonstrated", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTaskBrief_WithoutDeclarationAddsNoSectionForAnyRole()
    {
        var (kernel, goal) = CreateGoal("Use negative-control-revert: tests/A.cs in briefs.");
        foreach (var task in goal.Tasks)
            Assert.DoesNotContain("## Negative-control revert set", kernel.BuildTaskBrief(goal.Id, task.Id).Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void BuildTaskBrief_RejectedDeclarationIsVisible(AgentRole role)
    {
        var (kernel, goal) = CreateGoal("negative-control-revert: docs/a.md");
        var brief = kernel.BuildTaskBrief(goal.Id, goal.Tasks.Single(task => task.RequiredRole == role).Id).Content;
        Assert.Contains("declaration rejected: declaration-root-not-allowed", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTaskBrief_ShowsNewestFiveDistinctVariantReceiptsAcrossRoles()
    {
        var (kernel, goal) = CreateGoal(Declaration);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var start = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        for (var index = 0; index < 7; index++)
        {
            var receipt = new FindingEvidenceReceipt($"variant-{index}", Candidate,
                new FindingEvidenceRequest([new("Core.Tests", "SupportProbeTests")]), true, true, "green",
                Arms: [new(FindingEvidenceArm.SourceReverted, Candidate, FindingEvidenceArmDisposition.Green,
                    true, true, "green", RestoredPaths: ["tests/A/Support.cs"])],
                NegativeControlOutcome: FindingEvidenceNegativeControlOutcome.NotDemonstrated);
            var task = index % 2 == 0 ? tester : reviewer;
            task.RecordVerification(new TaskVerificationRecord("receipt", "C:\\repo", 0, "ok", "",
                start.AddMinutes(index), FindingEvidenceReceipts: [receipt, receipt]));
        }
        // A newer legacy receipt with no variant paths must not displace useful evidence.
        tester.RecordVerification(new TaskVerificationRecord("legacy", "C:\\repo", 0, "ok", "", start.AddMinutes(8),
            FindingEvidenceReceipts: [new("legacy", Candidate, new FindingEvidenceRequest([new("Core.Tests", "Legacy")]), true, true, "legacy")]));
        var lines = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content.Split('\n')
            .Where(line => line.StartsWith("revert_set:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, lines.Length);
        for (var index = 0; index < 5; index++)
            Assert.Contains($"receipt=variant-{6 - index};", lines[index], StringComparison.Ordinal);
    }

    private static (AgentOrchestratorKernel, Goal) CreateGoal(string objective)
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = new[] { AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer }
            .Select(role => new TaskSpec(TaskId.New(), "Inspect revert evidence", role)).ToArray();
        return (kernel, kernel.CreateGoal(objective, tasks));
    }
}
