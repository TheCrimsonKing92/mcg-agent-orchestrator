using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class AcceptanceMainAdvanceClassifierRepositoryWideTests
{
    [Xunit.Theory]
    [Xunit.InlineData("Directory.Build.props", "BuildSystem")]
    [Xunit.InlineData("src/bin/generated.cs", "GeneratedOrNoisy")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "SharedInfrastructure")]
    [Xunit.InlineData("scripts/verify.ps1", "Script")]
    [Xunit.InlineData("unmapped/file.bin", "Unknown")]
    public void Classifier_RepositoryWideLanding_FailsClosed(string landedPath, string expectedKind)
    {
        var disposition = AcceptanceMainAdvanceClassifier.Classify(
            "main-1",
            "main-2",
            ["src/candidate.cs"],
            ["src/candidate.cs"],
            ["src/candidate.cs"],
            [landedPath]);

        var overlap = Assert.IsType<AcceptanceMainAdvanceDisposition.Overlap>(disposition);
        Assert.Equal(expectedKind, overlap.Kind);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Directory.Build.props", "BuildSystem")]
    [Xunit.InlineData("src/bin/generated.cs", "GeneratedOrNoisy")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "SharedInfrastructure")]
    [Xunit.InlineData("scripts/verify.ps1", "Script")]
    [Xunit.InlineData("unmapped/file.bin", "Unknown")]
    public void Classifier_RepositoryWideCandidate_FailsClosed(string candidatePath, string expectedKind)
    {
        var disposition = AcceptanceMainAdvanceClassifier.Classify(
            "main-1",
            "main-2",
            [candidatePath],
            [candidatePath],
            [candidatePath],
            ["src/unrelated.cs"]);

        var overlap = Assert.IsType<AcceptanceMainAdvanceDisposition.Overlap>(disposition);
        Assert.Equal(candidatePath, overlap.VerifiedPath);
        Assert.Equal(expectedKind, overlap.Kind);
    }
}
