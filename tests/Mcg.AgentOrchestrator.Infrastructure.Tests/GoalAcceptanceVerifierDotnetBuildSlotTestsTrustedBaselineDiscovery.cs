using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_trusted_baseline_CS2012_retries_and_continues_without_failed_verdict")]
    public async Task GoalAcceptanceVerifierTrustedBaselineCs2012RetriesAndContinuesWithoutFailedVerdict()
    {
        var (root, mainRoot) = CreateTrustedBaselineWorkspace();
        var goalId = GoalId.New();
        var calls = new List<(string[] Args, string Worktree)>();
        var mainBuildAttempts = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "foreign-csc", null, false)],
            "test");
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
            {
                calls.Add((args, worktree));
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "The following Tests are available:\n    Sample.Tests.Passes"));
                }

                if (worktree == mainRoot && args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    mainBuildAttempts++;
                    if (mainBuildAttempts == 1)
                    {
                        var lockedPath = Path.Combine(GetArtifactsPath(args), "obj", "Sample.Tests.dll");
                        return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                            1,
                            $"CSC : error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."));
                    }
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                goalId,
                changedFiles: ["src/Sample.cs"]);

            Assert.True(result.Passed);
            Assert.True(result.Retried);
            Assert.Equal(2, mainBuildAttempts);
            var structural = Assert.Single(result.Checks!.Where(check => check.Name == "structural test coverage"));
            Assert.True(structural.Passed);
            Assert.True(structural.LockRemediationApplied);
            Assert.DoesNotContain(result.Checks!, check =>
                check.ResultSummary?.Contains("trusted main baseline build failed", StringComparison.OrdinalIgnoreCase) == true);
            var baselineCalls = calls
                .Where(call => call.Worktree == mainRoot &&
                    call.Args.Length >= 2 &&
                    call.Args[0] == "dotnet" &&
                    (call.Args[1] == "build" || call.Args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase)))
                .ToArray();
            Assert.NotEmpty(baselineCalls);
            foreach (var call in baselineCalls)
            {
                Assert.Equal(1, call.Args.Count(argument =>
                    argument.Equals("--artifacts-path", StringComparison.OrdinalIgnoreCase)));
                Assert.EndsWith(
                    Path.Combine("artifacts", "main-coverage-baseline"),
                    GetArtifactsPath(call.Args),
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            LockAttribution.AttributeForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_persistent_trusted_baseline_CS2012_is_infrastructure_deferral")]
    public async Task GoalAcceptanceVerifierPersistentTrustedBaselineCs2012IsInfrastructureDeferral()
    {
        var (root, mainRoot) = CreateTrustedBaselineWorkspace();
        var goalId = GoalId.New();
        var mainBuildAttempts = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        LockAttribution.AttributeForTests = (path, _) => new BuildLockAttribution(
            path,
            [new BuildLockHolder(null, "foreign-csc", null, false)],
            "test");
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
            {
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                if (args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "The following Tests are available:\n    Sample.Tests.Passes"));
                }

                if (worktree == mainRoot && args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    mainBuildAttempts++;
                    var lockedPath = Path.Combine(GetArtifactsPath(args), "obj", "Sample.Tests.dll");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        $"CSC : error CS2012: Cannot open '{lockedPath}' for writing because it is being used by another process."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var deferred = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(() => verifier.RunAsync(
                root,
                goalId,
                changedFiles: ["src/Sample.cs"]));

            Assert.Equal("trusted-main-build-lock", deferred.ReasonCode);
            Assert.NotNull(deferred.BuildLockAttribution);
            Assert.Equal(2, mainBuildAttempts);
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            LockAttribution.AttributeForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_candidate_discovery_compile_error_remains_failed_verdict")]
    public async Task GoalAcceptanceVerifierCandidateDiscoveryCompileErrorRemainsFailedVerdict()
    {
        var (root, mainRoot) = CreateTrustedBaselineWorkspace();
        var goalId = GoalId.New();
        var mainBuildAttempts = 0;
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
            {
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                if (worktree == root && args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        1,
                        "Build FAILED. error CS1002: ; expected"));
                }

                if (worktree == mainRoot && args.Length >= 2 && args[0] == "dotnet" && args[1] == "build")
                {
                    mainBuildAttempts++;
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            var result = await verifier.RunAsync(
                root,
                goalId,
                changedFiles: ["src/Sample.cs"]);

            Assert.False(result.Passed);
            var structural = Assert.Single(result.Checks!.Where(check =>
                check.ResultSummary == "candidate trusted discovery failed"));
            Assert.Contains("CS1002", structural.OutputTail, StringComparison.Ordinal);
            Assert.Equal(0, mainBuildAttempts);
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_trusted_discovery_enumerates_test_projects_omitted_from_manifest")]
    public void GoalAcceptanceVerifierTrustedDiscoveryEnumeratesTestProjectsOmittedFromManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-acceptance-project-discovery", Guid.NewGuid().ToString("N"));
        var mainRoot = Path.Combine(Path.GetTempPath(), "mcg-acceptance-project-discovery-main", Guid.NewGuid().ToString("N"));
        try
        {
            var coreProject = Path.Combine(root, "tests", "Core.Tests", "Core.Tests.csproj");
            var addedProject = Path.Combine(root, "tests", "Added", "Added.csproj");
            var supportProject = Path.Combine(root, "tests", "Support", "Support.csproj");
            var mainOnlyProject = Path.Combine(mainRoot, "tests", "Legacy.Tests", "Legacy.Tests.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(coreProject)!);
            Directory.CreateDirectory(Path.GetDirectoryName(addedProject)!);
            Directory.CreateDirectory(Path.GetDirectoryName(supportProject)!);
            Directory.CreateDirectory(Path.GetDirectoryName(mainOnlyProject)!);
            File.WriteAllText(coreProject, "<Project />");
            File.WriteAllText(
                addedProject,
                "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            File.WriteAllText(supportProject, "<Project />");
            File.WriteAllText(mainOnlyProject, "<Project />");

            var projects = GoalAcceptanceVerifier.DiscoverTrustedTestProjects(root, mainRoot);

            Assert.Equal(
                [
                    "tests/Added/Added.csproj",
                    "tests/Core.Tests/Core.Tests.csproj",
                    "tests/Legacy.Tests/Legacy.Tests.csproj"
                ],
                projects);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_scopes_deleted_test_files_to_their_project")]
    public void GoalAcceptanceVerifierScopesDeletedTestFilesToTheirProject()
    {
        var deleted = new[]
        {
            "tests/Core.Tests/FooTests.cs",
            "tests/Infrastructure.Tests/FooTests.cs",
            "tests/Infrastructure.Tests/Nested/BarTests.cs"
        };

        var core = GoalAcceptanceVerifier.DeletedTestFilesForProject(
            deleted,
            "tests/Core.Tests/Core.Tests.csproj");

        Assert.Equal(["tests/Core.Tests/FooTests.cs"], core);
    }

    [Xunit.Fact(DisplayName = "GoalAcceptanceVerifier_treats_rename_sources_as_deleted_from_their_former_project")]
    public void GoalAcceptanceVerifierTreatsRenameSourcesAsDeletedFromTheirFormerProject()
    {
        var deleted = GoalAcceptanceVerifier.ParseDeletedTestFilesForTests(
            """
            M	tests/Infrastructure.Tests/UnchangedTests.cs
            D	tests/Infrastructure.Tests/DeletedTests.cs
            R100	tests/Infrastructure.Tests/MovedTests.cs	tests/Infrastructure.Tests/Extracted/MovedTests.cs
            A	tests/Infrastructure.Tests/AddedTests.cs
            """,
            "tests/Infrastructure.Tests/Infrastructure.Tests.csproj",
            destination => destination.Contains("/Extracted/")
                ? "tests/Infrastructure.Tests/Extracted/Extracted.Tests.csproj"
                : "tests/Infrastructure.Tests/Infrastructure.Tests.csproj");

        Assert.Equal(
            [
                "tests/Infrastructure.Tests/DeletedTests.cs",
                "tests/Infrastructure.Tests/MovedTests.cs"
            ],
            deleted);
    }

    private static string? SetAcceptanceTimeoutEnvironment(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable);
        Environment.SetEnvironmentVariable(AcceptanceCheckTimeouts.EnvironmentVariable, value);
        return previous;
    }

    internal static (string CandidateRoot, string MainRoot) CreateTrustedBaselineWorkspace()
    {
        var candidateRoot = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 1,
                "enforceStructuralCoverage": true
              },
              "checks": [
                { "name": "sample tests", "type": "dotnet-test", "project": "tests/Sample.Tests/Sample.Tests.csproj", "arguments": ["--verbosity", "minimal"] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var mainRoot = Path.Combine(Path.GetTempPath(), "mcg-acceptance-main", Guid.NewGuid().ToString("N"));
        foreach (var root in new[] { candidateRoot, mainRoot })
        {
            var project = Path.Combine(root, "tests", "Sample.Tests", "Sample.Tests.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        }

        return (candidateRoot, mainRoot);
    }

    internal static bool IsVstestExecution(string[] args) =>
        args.Length >= 2 &&
        args[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        args[1].Equals("test", StringComparison.OrdinalIgnoreCase) &&
        !args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase);

    internal static void WriteVstestTrx(string[] args, string testName)
    {
        var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
        var loggerIndex = Array.IndexOf(args, "--logger");
        Assert.True(resultsDirectoryIndex >= 0 && resultsDirectoryIndex + 1 < args.Length);
        Assert.True(loggerIndex >= 0 && loggerIndex + 1 < args.Length);
        const string prefix = "trx;LogFileName=";
        Assert.StartsWith(prefix, args[loggerIndex + 1], StringComparison.OrdinalIgnoreCase);
        var resultsDirectory = args[resultsDirectoryIndex + 1];
        var fileName = args[loggerIndex + 1][prefix.Length..];
        Directory.CreateDirectory(resultsDirectory);
        File.WriteAllText(
            Path.Combine(resultsDirectory, fileName),
            $"<TestRun><TestDefinitions><UnitTest id=\"1\" name=\"{testName}\"><TestMethod className=\"Sample.Tests\" name=\"Passes\" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=\"1\" testName=\"{testName}\" outcome=\"Passed\" /></Results></TestRun>");
    }

}
