using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: stateless guidance calls; no environment mutation or shared resources.
public sealed class WorkerStackVerificationGuidanceTests
{
    [Fact]
    public void CurrentTask_Go_PreservesGoCommandsAndWorkerPolicy()
    {
        var lines = WorkerStackVerificationGuidance.CurrentTaskSection(ProjectStack.Go);

        Assert.Equal(6, lines.Count);
        Assert.Equal(string.Empty, lines[0]);
        Assert.Equal("## Build/Test Verification", lines[1]);
        Assert.Contains("`go build ./...`, `go vet ./...` and `go test ./...`", lines[2], StringComparison.Ordinal);
        Assert.Equal("- Do not build or test with `dotnet` or the .NET worker build helper; the orchestrator's `Submit-WorkerResult.ps1` submission command is required and is not a project build.", lines[4]);
        Assert.Equal("- The acceptance gate re-runs the Go checks and decides the verdict.", lines[5]);
    }

    [Fact]
    public void CurrentTask_NullStack_UsesNeutralGuidance()
    {
        string[] expected = [string.Empty, "## Build/Test Verification", .. WorkerNeutralStackGuidance.BuildTestLines];

        Assert.Equal(expected, WorkerStackVerificationGuidance.CurrentTaskSection(null));
    }

    [Fact]
    public void CurrentTask_Dotnet_PreservesBuildInvocationAndWorkerPolicy()
    {
        var invocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());

        var lines = WorkerStackVerificationGuidance.CurrentTaskSection(ProjectStack.Dotnet);

        Assert.Equal(6, lines.Count);
        Assert.Equal(string.Empty, lines[0]);
        Assert.Equal("## Build/Test Verification", lines[1]);
        Assert.StartsWith($"- Run `{invocation} <project.csproj> [project.csproj...]`", lines[2], StringComparison.Ordinal);
        Assert.Contains("only sanctioned worker-side .NET build check", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("- If no .NET project sources changed", lines[5], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProjectStack.Go)]
    [InlineData(null)]
    [InlineData(ProjectStack.Dotnet)]
    public void CurrentTask_RepeatedCalls_ReturnFreshEqualLists(ProjectStack? stack)
    {
        var first = WorkerStackVerificationGuidance.CurrentTaskSection(stack);
        var second = WorkerStackVerificationGuidance.CurrentTaskSection(stack);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(Toolchain.Dotnet, 7, "## Worker Build Check")]
    [InlineData(Toolchain.Go, 6, "## Go Verification")]
    [InlineData(Toolchain.Node, 5, "## Node.js Verification")]
    [InlineData(Toolchain.Python, 4, "## Python Verification")]
    [InlineData(Toolchain.Unknown, 4, "## Verification")]
    public void Deterministic_Toolchain_PreservesSectionShapeAndFreshness(
        Toolchain toolchain, int count, string heading)
    {
        var goalId = new GoalId("guidance-a");

        var lines = WorkerStackVerificationGuidance.DeterministicVerificationSection(toolchain, goalId);
        var second = WorkerStackVerificationGuidance.DeterministicVerificationSection(toolchain, goalId);

        Assert.Equal(count, lines.Count);
        Assert.Equal(string.Empty, lines[0]);
        Assert.Equal(heading, lines[1]);
        Assert.NotSame(lines, second);
        Assert.Equal(lines, second);
    }

    [Fact]
    public void Deterministic_DifferentGoals_ChangeOnlyArtifactsLine()
    {
        var goalA = new GoalId("guidance-a");
        // Artifact roots use eight-character goal prefixes; the control needs a distinct prefix.
        var goalB = new GoalId("other-b");

        var first = WorkerStackVerificationGuidance.DeterministicVerificationSection(Toolchain.Dotnet, goalA);
        var second = WorkerStackVerificationGuidance.DeterministicVerificationSection(Toolchain.Dotnet, goalB);

        Assert.Contains($"`{DotnetBuildStorageLayout.GoalArtifactsPath(goalA)}`", first[3], StringComparison.Ordinal);
        Assert.Contains($"`{DotnetBuildStorageLayout.GoalArtifactsPath(goalB)}`", second[3], StringComparison.Ordinal);
        Assert.NotEqual(first[3], second[3]);
        Assert.Equal(first.Count, second.Count);
        for (var index = 0; index < first.Count; index++)
        {
            if (index != 3)
                Assert.Equal(first[index], second[index]);
        }
    }

    [Fact]
    public void DeveloperBroker_RepeatedCalls_PreserveLinesAndReturnFreshLists()
    {
        var invocation = OrchestratorBuildEvidenceCheck.FormatScriptInvocation(OrchestratorBuildEvidenceCheck.ResolveScriptPath());

        var lines = WorkerStackVerificationGuidance.DeveloperBuildOnlyBrokerLines();
        var second = WorkerStackVerificationGuidance.DeveloperBuildOnlyBrokerLines();

        Assert.Equal(7, lines.Count);
        Assert.Equal("- build-only", lines[0]);
        Assert.Equal("  Artifact: current-task.md", lines[1]);
        Assert.Equal($"  Suggested command shape: `{invocation} <project.csproj> [project.csproj...]`", lines[3]);
        Assert.NotSame(lines, second);
        Assert.Equal(lines, second);
    }
}
