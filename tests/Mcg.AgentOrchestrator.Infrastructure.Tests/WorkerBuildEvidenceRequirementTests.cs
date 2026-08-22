using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerBuildEvidenceRequirementTests : WorkerDispatchTestSupport
{
    [Xunit.Fact]
    public void RequiredProjectsComeFromCommittedAndDirtyCompiledPaths()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var sourceRoot = Path.Combine(root, "src", "Feature");
        var testRoot = Path.Combine(root, "tests", "Feature.Tests");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(testRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "Feature.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(testRoot, "Feature.Tests.csproj"), "<Project />");

        var projects = WorkerBuildEvidenceRequirement.FindRequiredProjects(
            root,
            AgentRole.Developer,
            ["src/Feature/Feature.cs", "src/Feature/readme.md", ".orchestrator-context/state.cs"],
            ["src/Feature/Feature.cs", "tests/Feature.Tests/FeatureTests.cs", "deleted/missing.cs"]);

        Xunit.Assert.Equal(
            ["src/Feature/Feature.csproj", "tests/Feature.Tests/Feature.Tests.csproj"],
            projects);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void ReadOnlyRolesNeverOweBuildEvidence(AgentRole role)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Feature.csproj"), "<Project />");

        var projects = WorkerBuildEvidenceRequirement.FindRequiredProjects(
            root,
            role,
            ["src/Feature.cs"],
            []);

        Xunit.Assert.Empty(projects);
    }

    public static Xunit.TheoryData<string, bool> BuildEvidenceCases => new()
    {
        { "pass - build: 0 errors (Invoke-WorkerBuildCheck)", true },
        { "deferred - Invoke-WorkerBuildCheck passed with 0 errors; acceptance should run tests", true },
        { "pass - build passed after the fix", true },
        { "deferred - builds clean; tests belong to acceptance", true },
        { "pass - build completed with 0 errors", true },
        { "deferred - Invoke-WorkerBuildCheck", false },
        { "deferred - acceptance gate owns execution", false },
        { "fail - build: 1 error (Invoke-WorkerBuildCheck)", false },
        { "fail - build passed despite the reported failure", false },
        { "not-run - build passed", false }
    };

    [Xunit.Theory]
    [Xunit.MemberData(nameof(BuildEvidenceCases))]
    public void BuildEvidenceRecognitionUsesSemanticSuccessSignals(string tests, bool expected)
    {
        var workerResult = WorkerResultBlock("src/Feature.cs", "compile command", tests);

        Xunit.Assert.Equal(expected, WorkerDispatchCompletionClassifier.HasWorkerBuildEvidenceInText(workerResult));
    }
}
