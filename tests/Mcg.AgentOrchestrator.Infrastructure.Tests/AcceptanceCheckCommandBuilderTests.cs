using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceCheckCommandBuilderTests
{
    [Fact]
    public void DotnetDrivenCommands_ReceiveBuildEnvironmentArgumentsExactlyOnce()
    {
        var environment = CreateBuildEnvironment();
        var arguments = new[]
        {
            "dotnet",
            "test",
            "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
            "--no-build"
        };

        var effective = AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments(arguments, environment);

        Assert.Equal(1, CountExact(effective, "--artifacts-path"));
        Assert.Equal(1, CountPrefix(effective, "-maxcpucount:"));
        Assert.Equal(1, CountExact(effective, "-p:BuildInParallel=false"));
        Assert.Equal(
            [
                "dotnet",
                "test",
                "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj",
                "--no-build",
                "--artifacts-path",
                environment.ArtifactsPath,
                "-maxcpucount:4",
                "-p:BuildInParallel=false"
            ],
            effective);
    }

    [Fact]
    public void DirectMtpExecutables_NeverReceiveMsBuildShapedArguments()
    {
        var environment = CreateBuildEnvironment();
        var executableCommand = AcceptanceCheckCommandBuilder.UseDotnetHostForManagedExecutable(
        [
            @"C:\artifacts\bin\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.exe",
            "--no-ansi",
            "--progress",
            "off",
            "--filter-class",
            "*RepositoryChangeClassifierTests*"
        ]);
        var dllCommand = AcceptanceCheckCommandBuilder.UseDotnetHostForManagedExecutable(
        [
            @"C:\artifacts\bin\Mcg.AgentOrchestrator.Core.Tests\Mcg.AgentOrchestrator.Core.Tests.dll",
            "--list-tests",
            "json"
        ]);

        var executableEffective = AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments(
            executableCommand,
            environment);
        var dllEffective = AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments(dllCommand, environment);

        Assert.Equal(executableCommand, executableEffective);
        Assert.Equal(dllCommand, dllEffective);
        Assert.Equal("dotnet", dllEffective[0], ignoreCase: true);
        Assert.EndsWith("Mcg.AgentOrchestrator.Core.Tests.dll", dllEffective[1], StringComparison.OrdinalIgnoreCase);
        AssertNoMsBuildShapedArguments(executableEffective);
        AssertNoMsBuildShapedArguments(dllEffective);
    }

    [Fact]
    public void GitCommands_NeverReceiveMsBuildShapedArguments()
    {
        var environment = CreateBuildEnvironment();
        var arguments = new[] { "git", "grep", "-q", "--", "AcceptanceCheckCommandBuilder" };

        var effective = AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments(arguments, environment);

        Assert.Equal(arguments, effective);
        AssertNoMsBuildShapedArguments(effective);
    }

    [Fact]
    public void SupportedFilters_TranslateIdentically()
    {
        Assert.Equal(
            ["--filter-class", "*CliCommandTests*"],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter("FullyQualifiedName~CliCommandTests"));
        Assert.Equal(
            ["--filter-not-class", "*CliCommandTestsGoalLifecycleCleanupHooks*"],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter(
                "FullyQualifiedName!~CliCommandTestsGoalLifecycleCleanupHooks"));
        Assert.Equal(
            ["--filter-not-trait", "Category=HostIntegration"],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter("Category!=HostIntegration"));
        Assert.Equal(
            [
                "--filter-class",
                "*CliCommandTests*",
                "--filter-not-class",
                "*CliCommandTestsGoalLifecycleCleanupHooks*",
                "--filter-not-trait",
                "Category=HostIntegration"
            ],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter(
                "FullyQualifiedName~CliCommandTests&FullyQualifiedName!~CliCommandTestsGoalLifecycleCleanupHooks&Category!=HostIntegration"));
        Assert.Equal(
            [
                "--filter-class",
                "*GoalsPruneTests*",
                "--filter-class",
                "*LandingExecutorTests*"
            ],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter(
                "FullyQualifiedName~GoalsPruneTests|FullyQualifiedName~LandingExecutorTests"));
        Assert.Equal(
            [
                "--filter-class",
                "*ChaosGate*",
                "--filter-class",
                "*InquiryDispatcherTests*",
                "--filter-not-trait",
                "Category=HostIntegration"
            ],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter(
                "(FullyQualifiedName~ChaosGate|FullyQualifiedName~InquiryDispatcherTests)&Category!=HostIntegration"));
        Assert.Equal(
            ["--filter-class", "*WorkerDispatchTestsModelSelection*"],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter("FullyQualifiedName ~ WorkerDispatchTestsModelSelection"));
    }

    [Fact]
    public void UnsupportedFilterToken_FailsLoudlyRatherThanMatchingNothing()
    {
        const string filter = "FullyQualifiedName=CliCommandTests";

        var exception = Assert.Throws<InvalidOperationException>(
            () => AcceptanceCheckCommandBuilder.TranslateMtpFilter(filter).ToArray());

        Assert.Equal(
            "MTP test filter 'FullyQualifiedName=CliCommandTests' contains unsupported token 'FullyQualifiedName=CliCommandTests'.",
            exception.Message);
    }

    [Fact]
    public void ArgumentOrdering_IsUnchanged()
    {
        var environment = CreateBuildEnvironment();
        var arguments = new[]
        {
            "dotnet",
            "build",
            "Mcg.AgentOrchestrator.sln",
            "--nologo",
            "-v",
            "quiet"
        };

        var effective = AcceptanceCheckCommandBuilder.WithBuildEnvironmentArguments(arguments, environment);

        Assert.Equal(arguments, effective.Take(arguments.Length));
        Assert.Equal(environment.Arguments, effective.Skip(arguments.Length));
        Assert.Equal(
            [
                "--filter-class",
                "*AlphaTests*",
                "--filter-not-class",
                "*BetaTests*",
                "--filter-not-trait",
                "Category=HostIntegration"
            ],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter(
                "FullyQualifiedName~AlphaTests|FullyQualifiedName!~BetaTests&Category!=HostIntegration"));
    }

    private static DotnetBuildEnvironment CreateBuildEnvironment() =>
        new(
            "lease",
            @"C:\artifacts\root",
            @"C:\artifacts\root\artifacts",
            @"C:\artifacts\root\lease.lock",
            [
                "--artifacts-path",
                @"C:\artifacts\root\artifacts",
                "-maxcpucount:4",
                "-p:BuildInParallel=false"
            ],
            "lease");

    private static void AssertNoMsBuildShapedArguments(IReadOnlyList<string> arguments)
    {
        Assert.Equal(0, CountExact(arguments, "--artifacts-path"));
        Assert.Equal(0, CountPrefix(arguments, "-maxcpucount"));
        Assert.Equal(0, CountExact(arguments, "-p:BuildInParallel=false"));
        Assert.Equal(0, CountPrefix(arguments, "-p:"));
        Assert.Equal(0, CountPrefix(arguments, "/p:"));
    }

    private static int CountExact(IReadOnlyList<string> arguments, string value) =>
        arguments.Count(argument => argument.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static int CountPrefix(IReadOnlyList<string> arguments, string prefix) =>
        arguments.Count(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
