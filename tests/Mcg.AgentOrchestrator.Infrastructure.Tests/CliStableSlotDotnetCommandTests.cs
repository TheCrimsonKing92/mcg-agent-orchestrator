using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class CliStableSlotDotnetCommandTests
{
    [Fact]
    public void TranslateMtpFilter_PreservesClassOrderAndNegation()
    {
        Assert.Equal(
            ["--filter-class", "*Alpha*", "--filter-not-class", "*Beta.Gamma*", "--filter-class", "*Delta*"],
            CliStableSlotDotnetCommand.TranslateMtpFilter(
                "FullyQualifiedName~Alpha|fullyqualifiedname !~ Beta.Gamma&Delta"));
    }

    [Theory]
    [InlineData("Category=Unit")]
    [InlineData("FullyQualifiedName=Alpha")]
    public void TranslateMtpFilter_RejectsUnsupportedTokens(string filter)
    {
        var error = Assert.Throws<ArgumentException>(() => CliStableSlotDotnetCommand.TranslateMtpFilter(filter));

        Assert.Contains("Unsupported stable-slot MTP filter", error.Message);
        Assert.Contains(filter, error.Message);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ShouldBuildMtpProject_RequiresBothNoBuildAndExistingArtifactsToSkip(
        bool noBuild,
        bool artifactsExist,
        bool expected)
    {
        Assert.Equal(expected, CliStableSlotDotnetCommand.ShouldBuildMtpProject(noBuild, artifactsExist));
    }

    [Fact]
    public void ResolveResultsDirectory_UsesDefaultOrWorkspaceRelativeOrRootedPath()
    {
        var workspace = Path.GetFullPath("stable-slot-workspace");
        var defaultResults = Path.Combine(workspace, "TestResults");
        var elsewhere = Path.Combine(workspace, "elsewhere");

        Assert.Equal(Path.GetFullPath(defaultResults),
            CliStableSlotDotnetCommand.ResolveResultsDirectory(null, workspace, defaultResults));
        Assert.Equal(Path.Combine(workspace, "focused"),
            CliStableSlotDotnetCommand.ResolveResultsDirectory("focused", workspace, defaultResults));
        Assert.Equal(elsewhere,
            CliStableSlotDotnetCommand.ResolveResultsDirectory(elsewhere, workspace, defaultResults));
    }

    [Fact]
    public void ReadOption_ScansOnlyOptionsAfterTheProjectWithFollowingValues()
    {
        var readOption = typeof(CliStableSlotDotnetCommand).GetMethod(
            "ReadOption", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.NotNull(readOption);
        string[] parts =
        [
            "stable-slot-dotnet", "mtp-test", "tests/Sample.Tests/Sample.Tests.csproj",
            "--filter", "Alpha", "--RESULTS-DIRECTORY", "out"
        ];

        Assert.Equal("Alpha", readOption.Invoke(null, [parts, "--filter"]));
        Assert.Equal("out", readOption.Invoke(null, [parts, "--results-directory"]));
        Assert.Null(readOption.Invoke(null,
            [new[] { "stable-slot-dotnet", "mtp-test", "--filter", "Alpha" }, "--filter"]));
        Assert.Null(readOption.Invoke(null,
            [new[] { "stable-slot-dotnet", "mtp-test", "tests/Sample.Tests/Sample.Tests.csproj", "--no-build", "--filter" }, "--filter"]));
    }

    [Fact]
    public void Run_ValidatesArgumentCountBeforeReadingContext()
    {
        var error = Assert.Throws<ArgumentException>(() => CliStableSlotDotnetCommand.Run(["stable-slot-dotnet"], null!));

        Assert.Equal("Usage: stable-slot-dotnet <dotnet-arguments>", error.Message);
    }
}
