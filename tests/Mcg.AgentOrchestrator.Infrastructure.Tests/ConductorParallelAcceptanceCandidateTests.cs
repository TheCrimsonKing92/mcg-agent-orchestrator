using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorParallelAcceptanceCandidateTests
{
    private static readonly string[] Goal424f2d45Files =
    [
        "docs/test-design-discipline.md",
        "src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardResponseDtos.Reports.cs",
        "src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardResponseDtos.Tasks.cs",
        "src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardResponseMapper.Reports.cs",
        "src/Mcg.AgentOrchestrator.App/Dashboard/Api/DashboardResponseMapper.Tasks.cs",
        "src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Processes/BackgroundDispatchRunner.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Processes/DispatchProcessHost.cs"
    ];

    private static readonly string[] GoalBadac7c6Files =
    [
        "docs/test-design-discipline.md",
        "src/Mcg.AgentOrchestrator.App/Cli/CliArgumentParser.NormalizeArgs.cs",
        "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.System.cs",
        "src/Mcg.AgentOrchestrator.App/Cli/ConsoleViews.Attention.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/GoalRefinementService.cs"
    ];

    [Fact]
    public void ReportedDocumentationIntersection_DoesNotOverlapEitherDirection()
    {
        var goal = CreateGoal();
        var first = ConductorParallelAcceptanceCandidate.Create(goal, 0, Goal424f2d45Files);
        var second = ConductorParallelAcceptanceCandidate.Create(goal, 1, GoalBadac7c6Files);

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
        Assert.DoesNotContain(first.ResourceKeys, key =>
            key.Equals("ownership:docs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(second.ResourceKeys, key =>
            key.Equals("ownership:docs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["docs/test-design-discipline.md"],
            first.GetDocumentationExclusionEvidence(second));
    }

    [Fact]
    public void SharedSourcePath_Overlaps()
    {
        var goal = CreateGoal();
        var first = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"]);
        var second = ConductorParallelAcceptanceCandidate.Create(
            goal,
            1,
            ["src/Mcg.AgentOrchestrator.Core/Application/DispatchFailureClassifier.cs"]);

        Assert.True(first.Overlaps(second));
    }

    [Fact]
    public void SourceDirectoryPrefix_Overlaps()
    {
        var goal = CreateGoal();
        var directory = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/Mcg.AgentOrchestrator.App/Orchestration"]);
        var file = ConductorParallelAcceptanceCandidate.Create(
            goal,
            1,
            ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"]);

        Assert.True(directory.Overlaps(file));
        Assert.True(file.Overlaps(directory));
    }

    [Fact]
    public void DisjointTestFilesInSameProject_DoNotOverlap()
    {
        var goal = CreateGoal();
        var first = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs"]);
        var second = ConductorParallelAcceptanceCandidate.Create(
            goal,
            1,
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorParallelAcceptanceCandidateTests.cs"]);

        Assert.NotEmpty(first.ResourceKeys);
        Assert.NotEmpty(second.ResourceKeys);
        Assert.Empty(first.ResourceKeys.Intersect(second.ResourceKeys, StringComparer.OrdinalIgnoreCase));
        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
    }

    [Fact]
    public void SameTestFile_OverlapsThroughExactPathReservation()
    {
        const string path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs";
        const string expectedResource =
            "ownership:test-project:tests/mcg.agentorchestrator.infrastructure.tests/workerdispatchtests.cs";
        var goal = CreateGoal();
        var first = ConductorParallelAcceptanceCandidate.Create(goal, 0, [path]);
        var second = ConductorParallelAcceptanceCandidate.Create(goal, 1, [path]);

        Assert.Contains(first.ResourceKeys, key => key.Equals(expectedResource, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(second.ResourceKeys, key => key.Equals(expectedResource, StringComparison.OrdinalIgnoreCase));
        Assert.True(first.Overlaps(second));
        Assert.True(second.Overlaps(first));
    }

    [Fact]
    public void UnknownScopes_ReserveConservativelyAndOverlap()
    {
        var goal = CreateGoal();
        var first = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
        var second = ConductorParallelAcceptanceCandidate.Create(goal, 1, []);

        Assert.Equal(["ownership:unknown-acceptance-scope"], first.ResourceKeys);
        Assert.Equal(["ownership:unknown-acceptance-scope"], second.ResourceKeys);
        Assert.True(first.Overlaps(second));
        Assert.True(second.Overlaps(first));
    }

    [Fact]
    public void DistinctMalformedTestPaths_ReserveConservativelyAndOverlap()
    {
        var goal = CreateGoal();
        var first = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["tests/LooseTests.cs"]);
        var second = ConductorParallelAcceptanceCandidate.Create(goal, 1, ["tests/OtherLoose.cs"]);

        Assert.Equal(["ownership:unknown-acceptance-scope"], first.ResourceKeys);
        Assert.Equal(["ownership:unknown-acceptance-scope"], second.ResourceKeys);
        Assert.True(first.Overlaps(second));
        Assert.True(second.Overlaps(first));
    }

    [Fact]
    public void DocumentationOnlyScope_ReservesNothingAndNeverOverlaps()
    {
        var goal = CreateGoal();
        var first = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["docs/negative-controls/first.txt", "notes/first.md"]);
        var second = ConductorParallelAcceptanceCandidate.Create(
            goal,
            1,
            ["docs/negative-controls/second.txt", "notes/second.MD"]);
        var unknown = ConductorParallelAcceptanceCandidate.Create(goal, 2, []);

        Assert.Empty(first.ConflictScopePaths);
        Assert.Empty(first.ResourceKeys);
        Assert.Empty(second.ResourceKeys);
        Assert.Equal(["ownership:unknown-acceptance-scope"], unknown.ResourceKeys);
        Assert.False(first.Overlaps(second));
        Assert.False(first.Overlaps(unknown));
    }

    [Fact]
    public void ScopePaths_KeepExcludedDocumentation()
    {
        var goal = CreateGoal();
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["docs\\test-design-discipline.md", "src/Feature.cs"]);

        Assert.Equal(["docs/test-design-discipline.md", "src/Feature.cs"], candidate.ScopePaths);
        Assert.Equal(["src/Feature.cs"], candidate.ConflictScopePaths);
        Assert.Equal(["docs/test-design-discipline.md"], candidate.ExcludedConflictScopePaths);
    }

    [Theory]
    [InlineData("docs/readme.txt", true)]
    [InlineData("Docs\\guide.txt", true)]
    [InlineData("README.md", true)]
    [InlineData("nested/README.MD", true)]
    [InlineData(".github/readme.txt", false)]
    [InlineData("notes.txt", false)]
    [InlineData("src/docs/readme.txt", false)]
    [InlineData("docsish/readme.txt", false)]
    public void DocumentationExclusion_UsesExactPathRule(string path, bool expected)
    {
        Assert.Equal(
            expected,
            ConductorParallelAcceptanceCandidate.IsDocumentationExcludedFromConflict(path));
    }

    private static Goal CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        return GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Test parallel acceptance overlap");
    }
}
