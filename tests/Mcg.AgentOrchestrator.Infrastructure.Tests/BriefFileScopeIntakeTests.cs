using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each test owns a GUID temp root and removes it in finally.
public sealed class BriefFileScopeIntakeTests
{
    [Fact]
    public void Planner_RepositoryRoot_FiltersScopesAndSelectsGoTools()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "record"));
        try
        {
            File.WriteAllText(Path.Combine(root, "record/record.go"), "package record");
            var text = BriefScopeSectionSelectorTests.FixtureD0;

            var filtered = GoalObjectivePlanner.Build(text, repositoryRoot: root);
            var unfiltered = GoalObjectivePlanner.Build(text);

            Assert.Equal(["cmd/crosswalk/main.go", "record/record.go"], filtered.FileScopes);
            Assert.Contains("go build / go test ./...", filtered.RequiredTools);
            Assert.DoesNotContain("npm/yarn (package.json scripts)", filtered.RequiredTools);
            Assert.Equal(GoalFileScopeInference.FromText(text).Select(scope => scope.Path), unfiltered.FileScopes);
            Assert.Equal(unfiltered.CanCreateGoal, filtered.CanCreateGoal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Planner_MissingPath_FiltersScopesWithoutChangingAmbiguity()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string text = "The widget in src/Feature/Missing.cs renders twice.";

            var filtered = GoalObjectivePlanner.Build(text, repositoryRoot: root);
            var unfiltered = GoalObjectivePlanner.Build(text);

            Assert.Empty(filtered.FileScopes);
            Assert.Equal(["src/Feature/Missing.cs"], unfiltered.FileScopes);
            Assert.Equal(unfiltered.CanCreateGoal, filtered.CanCreateGoal);
            Assert.Contains("scope-implicit", filtered.RiskLabels);
            Assert.DoesNotContain("scope-implicit", unfiltered.RiskLabels);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Advisor_EvidenceCitations_OnlyCollidesWithScopeList()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var path in BriefScopeSectionSelectorTests.FixtureBPaths.Append("config/acceptance-manifest.json"))
            {
                var fullPath = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, "fixture");
            }

            var goal1 = MakeGoal("Change config/acceptance-manifest.json.");
            var goal2 = MakeGoal("Change src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.OwnedRoots.cs.");
            var goal3 = MakeGoal("Change src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.SharedGoalRoots.cs.");
            Goal[] goals = [goal1, goal2, goal3];

            var unfiltered = GoalScopeCollisionAdvisor.Build([BriefScopeSectionSelectorTests.FixtureB], goals);
            var filtered = GoalScopeCollisionAdvisor.Build(
                [BriefScopeSectionSelectorTests.FixtureB], goals, repositoryRoot: root);

            Assert.Equal([goal2.Id.Value], unfiltered.ConflictingGoalIds);
            Assert.Equal([goal2.Id.Value], filtered.ConflictingGoalIds);
            Assert.Equal(BriefScopeSectionSelectorTests.FixtureBPaths,
                filtered.ProposedScopes.Select(scope => scope.Path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Advisor_MissingProposedPath_RemovesCollisionOnlyWhenRootSupplied()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src/Feature"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src/Feature/Present.cs"), "fixture");
            const string text = """
                ## Scope
                Files this goal may change:
                - `src/Feature/Present.cs`;
                - `src/Feature/Gone.cs`.
                """;
            var goal = MakeGoal("Change src/Feature/Gone.cs.");

            var filtered = GoalScopeCollisionAdvisor.Build([text], [goal], repositoryRoot: root);
            var unfiltered = GoalScopeCollisionAdvisor.Build([text], [goal]);

            Assert.Empty(filtered.Collisions);
            Assert.Equal(["src/Feature/Present.cs"], filtered.ProposedScopes.Select(scope => scope.Path));
            var collision = Assert.Single(unfiltered.Collisions);
            Assert.Equal(ScopeCollisionKind.ExactFile, collision.Kind);
            Assert.Equal(goal.Id.Value, collision.GoalId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static Goal MakeGoal(string objective) => new(
        GoalId.New(), objective, [new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer)]);
}
