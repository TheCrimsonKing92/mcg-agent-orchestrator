using Mcg.AgentOrchestrator.App.Orchestration;

// Each fixture isolates its files and SQLite store, so these tests are parallel-safe.
public sealed class BoardFillScopeDependsTestsTestTreePrefix
{
    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/SampleTests.cs")]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/SampleTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/")]
    [InlineData("tests/", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SampleTests.cs")]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SampleTests.cs", "tests/")]
    [InlineData(@"TESTS\Mcg.AgentOrchestrator.Infrastructure.Tests\",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SampleTests.cs")]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SampleTests.cs",
        @"TESTS\Mcg.AgentOrchestrator.Infrastructure.Tests\")]
    public void Test_tree_prefix_in_either_orientation_omits_dependency_keeps_verdict(
        string activePath, string draftPath)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var goal = h.Kernel.CreateGoal($"Change {activePath} only.");
        var draft = $"Change {draftPath} only.";
        var goals = h.Kernel.Goals.ToArray();
        var report = GoalScopeCollisionAdvisor.Build([draft], goals, h.Item.Id);
        var collision = Assert.Single(report.Collisions);
        Assert.Equal(goal.Id.Value, collision.GoalId);
        Assert.Equal(ScopeCollisionKind.DirectoryPrefix, collision.Kind);
        Assert.StartsWith("overlap-detected", report.VerdictToken);

        var proposal = BoardFillScopeDepends.Propose(draft, goals, h.Item.Id);

        Assert.Empty(proposal.Depends);
        Assert.Equal(report.VerdictToken, proposal.Verdict);
    }

    [Fact]
    public void Mixed_test_prefix_and_exact_source_collision_keeps_only_exact_reason()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var goal = h.Kernel.CreateGoal(
            "Change tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ and src/Feature/File.cs.");
        const string draft =
            "Change tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SampleTests.cs and src/Feature/File.cs.";
        var goals = h.Kernel.Goals.ToArray();
        var report = GoalScopeCollisionAdvisor.Build([draft], goals, h.Item.Id);
        Assert.Equal(2, report.Collisions.Count);
        Assert.Contains(report.Collisions, collision => collision.Kind == ScopeCollisionKind.DirectoryPrefix);
        Assert.Contains(report.Collisions, collision => collision.Kind == ScopeCollisionKind.ExactFile);

        var proposal = BoardFillScopeDepends.Propose(draft, goals, h.Item.Id);

        var dependency = Assert.Single(proposal.Depends);
        Assert.Equal(goal.Id.Value, dependency.GoalId);
        Assert.Equal("ExactFile src/Feature/File.cs<->src/Feature/File.cs", dependency.Reason);
    }

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/", "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs")]
    [InlineData("scripts/", "scripts/Sample.ps1")]
    [InlineData("docs/", "docs/Sample.md")]
    [InlineData("docs/tests/", "docs/tests/Sample.md")]
    [InlineData("src/Feature/Tests/", "src/Feature/Tests/Sample.cs")]
    public void Prefix_outside_root_test_tree_still_proposes_dependency(string activePath, string draftPath)
    {
        using var h = new BoardFillAssessmentTestFixture();
        var goal = h.Kernel.CreateGoal($"Change {activePath} and tests/Suppressed/.");
        var draft = $"Change {draftPath} and tests/Suppressed/NoiseTests.cs.";
        var goals = h.Kernel.Goals.ToArray();
        var report = GoalScopeCollisionAdvisor.Build([draft], goals, h.Item.Id);
        Assert.Equal(2, report.Collisions.Count);
        Assert.All(report.Collisions, collision => Assert.Equal(ScopeCollisionKind.DirectoryPrefix, collision.Kind));

        var proposal = BoardFillScopeDepends.Propose(draft, goals, h.Item.Id);

        var dependency = Assert.Single(proposal.Depends);
        Assert.Equal(goal.Id.Value, dependency.GoalId);
        Assert.Equal($"DirectoryPrefix {draftPath}<->{activePath}", dependency.Reason);
    }

    [Fact]
    public void Exact_test_file_collision_still_proposes_dependency()
    {
        using var h = new BoardFillAssessmentTestFixture();
        const string path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SampleTests.cs";
        var goal = h.Kernel.CreateGoal($"Change {path} and tests/Suppressed/.");
        var draft = $"Change {path} and tests/Suppressed/NoiseTests.cs.";
        var goals = h.Kernel.Goals.ToArray();
        var report = GoalScopeCollisionAdvisor.Build([draft], goals, h.Item.Id);
        Assert.Equal(2, report.Collisions.Count);
        Assert.Contains(report.Collisions, collision => collision.Kind == ScopeCollisionKind.DirectoryPrefix);
        Assert.Contains(report.Collisions, collision => collision.Kind == ScopeCollisionKind.ExactFile);

        var proposal = BoardFillScopeDepends.Propose(draft, goals, h.Item.Id);

        var dependency = Assert.Single(proposal.Depends);
        Assert.Equal(goal.Id.Value, dependency.GoalId);
        Assert.Equal($"ExactFile {path}<->{path}", dependency.Reason);
    }
}
