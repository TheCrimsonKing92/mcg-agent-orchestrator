using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemovedTestProject : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Project = "tests/Removed.Tests/Removed.Tests.csproj";
    private const string Prefix = "test project removed without manifest removal and owner approval: " + Project + " (3 tests on main)";

    [Xunit.Fact]
    public async Task GoalAcceptanceVerifier_removed_test_project_records_approved_manifest_removal()
    {
        // HEAD efbc2b490 resolves the absent candidate invocation and throws InvalidDataException;
        // execution of this regression is owned by the conductor, per the Developer brief.
        var (result, calls) = await RunRemovedAsync(false, false, true);
        Assert.True(result.Passed);
        Assert.Equal("structural test coverage", result.Name);
        Assert.Equal("removed test project: " + Project + " (3 tests on main)", result.ResultSummary);
        Assert.Null(result.FailureClassification);
        Assert.All(calls, call => Assert.Equal("main", call.Side));
        var discovery = Assert.Single(calls.Where(call => call.Args.Contains("--list-tests")));
        Assert.Equal("dotnet", discovery.Args[0]);
        Assert.EndsWith(Path.Combine("main-only", "Removed.Tests", "debug", "Removed.Tests.dll"), discovery.Args[1]);
        Assert.DoesNotContain(discovery.Args, arg => arg.Contains("candidate-only", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Args.Length > 2 && call.Args[1] == "build" && call.Args[2] == Project);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, false, true, "check")]
    [Xunit.InlineData(false, true, true, "mtp invocation")]
    [Xunit.InlineData(true, true, true, "check and mtp invocation")]
    [Xunit.InlineData(false, false, false, null)]
    [Xunit.InlineData(true, true, false, "check and mtp invocation")]
    public async Task RemovedProjectFailsWithNamedMissingConditions(
        bool hasCheck, bool hasInvocation, bool approved, string? declaredKind)
    {
        var (result, calls) = await RunRemovedAsync(hasCheck, hasInvocation, approved);
        Assert.False(result.Passed);
        Assert.StartsWith("structural test coverage: ", result.Name);
        Assert.Equal(AcceptanceFailureClassifications.StructuralCoverageFailed, result.FailureClassification);
        Assert.StartsWith(Prefix, result.OutputTail);
        if (declaredKind is not null)
            Assert.Contains("missing manifest removal: candidate manifest still declares " + declaredKind, result.OutputTail);
        else
            Assert.DoesNotContain("missing manifest removal", result.OutputTail);
        if (!approved)
            Assert.Contains("missing owner approval: no owner approval (approve-policy-change)", result.OutputTail);
        else
            Assert.DoesNotContain("missing owner approval", result.OutputTail);
        Assert.All(calls, call => Assert.Equal("main", call.Side));
        var discovery = Assert.Single(calls.Where(call => call.Args.Contains("--list-tests")));
        Assert.Contains("main-only", discovery.Args[1]);
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-invocation", "trusted-main-discovery-failed")]
    [Xunit.InlineData("malformed-manifest", "trusted-main-discovery-failed")]
    [Xunit.InlineData("build-failed", "trusted-main-build-failed")]
    [Xunit.InlineData("discovery-failed", "trusted-main-discovery-failed")]
    [Xunit.InlineData("discovery-timeout", "trusted-main-discovery-timeout")]
    [Xunit.InlineData("discovery-io", "trusted-main-discovery-io")]
    public async Task RemovedProjectKeepsTrustedMainInfrastructureDeferrals(string fault, string code)
    {
        var deferred = await Assert.ThrowsAsync<AcceptanceInfrastructureDeferredException>(
            () => RunRemovedAsync(false, false, true, fault));
        Assert.Equal(code, deferred.ReasonCode);
    }

    [Xunit.Fact]
    public async Task MultipleRemovedProjectsAreRecordedIndependently()
    {
        const string second = "tests/Second.Tests/Second.Tests.csproj";
        var (result, calls) = await RunRemovedAsync(false, false, true, secondProject: second);
        Assert.True(result.Passed);
        Assert.Contains("removed test project: " + Project + " (3 tests on main)", result.ResultSummary);
        Assert.Contains("removed test project: " + second + " (3 tests on main)", result.ResultSummary);
        Assert.Equal(2, calls.Count(call => call.Args.Contains("--list-tests")));

        var (failed, failedCalls) = await RunRemovedAsync(false, false, true,
            secondProject: second, keepSecondCheck: true);
        Assert.False(failed.Passed);
        Assert.StartsWith("test project removed without manifest removal and owner approval: " + second, failed.OutputTail);
        Assert.Contains("removed test project: " + Project + " (3 tests on main)", failed.ResultSummary);
        Assert.Equal(2, failedCalls.Count(call => call.Args.Contains("--list-tests")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public async Task BothSidesKeepDiscoveryArgumentsAndCountFloorRegardlessOfApproval(int mainCount)
    {
        var (root, mainRoot) = CreateTrustedBaselineWorkspace();
        var goal = GoalId.New();
        var calls = new List<(string[] Args, string Side)>();
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
            {
                calls.Add((args, worktree == mainRoot ? "main" : "candidate"));
                if (IsVstestExecution(args))
                {
                    WriteVstestTrx(args, "Sample.Tests.Passes");
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }
                if (args.Contains("--list-tests"))
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        "The following Tests are available:\n    Sample.Tests.Passes" +
                        (worktree == mainRoot && mainCount == 2 ? "\n    Sample.Tests.AlsoPasses" : "")));
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });
            var original = await verifier.RunAsync(root, goal, changedFiles: ["src/Sample.cs"]);
            var baseline = Assert.Single(original.Checks!.Where(check => check.Name.StartsWith("structural test coverage")));
            Assert.Equal(mainCount == 1, baseline.Passed);
            var expected = DiscoveryCalls(calls);
            Assert.Equal(2, expected.Length);
            foreach (var approved in new[] { false, true })
            {
                calls.Clear();
                var actual = await verifier.RunStructuralCoverageForTests(root, goal, ["src/Sample.cs"],
                    approved, original.Checks!);
                Assert.Equal(baseline.Passed, actual.Passed);
                Assert.Equal(baseline.ResultSummary, actual.ResultSummary);
                Assert.Equal(baseline.FailureClassification, actual.FailureClassification);
                Assert.Equal(expected, DiscoveryCalls(calls));
            }
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    private static string[] DiscoveryCalls(List<(string[] Args, string Side)> calls) => calls
        .Where(call => call.Args.Contains("--list-tests"))
        .Select(call =>
        {
            var args = call.Args.ToArray();
            // Artifact lease paths differ by invocation; preserve every argument and the baseline suffix.
            var index = Array.IndexOf(args, "--artifacts-path");
            if (index >= 0) args[index + 1] = call.Side == "main" ? "artifacts/main-coverage-baseline" : "artifacts";
            return call.Side + ":" + JsonSerializer.Serialize(args);
        }).ToArray();

    private async Task<(AcceptanceCheckResult Result, List<(string[] Args, string Side)> Calls)> RunRemovedAsync(
        bool hasCheck, bool hasInvocation, bool approved, string? fault = null,
        string? secondProject = null, bool keepSecondCheck = false)
    {
        var root = CreateManifestWorkspace("""{"version":1,"engine":{},"checks":[],"forbiddenChangedPathGlobs":[]}""");
        var mainRoot = InfrastructureTestSupport.CreateTempDirectory();
        var goal = GoalId.New();
        var calls = new List<(string[] Args, string Side)>();
        var projects = secondProject is null ? new[] { Project } : new[] { Project, secondProject };
        foreach (var project in projects)
        {
            var path = Path.Combine(mainRoot, project.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<Project><PropertyGroup><IsTestProject>true</IsTestProject><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup></Project>");
        }
        WriteManifest(root, projects.Where(project => project == Project ? hasCheck : keepSecondCheck),
            hasInvocation ? [Project] : [], "candidate-only");
        WriteManifest(mainRoot, [], fault == "missing-invocation" ? [] : projects, "main-only");
        if (fault == "malformed-manifest") File.WriteAllText(Path.Combine(mainRoot, "config", "acceptance-manifest.json"), "{invalid");
        TestOverrides.ResolveMainWorktreePathForTests = _ => mainRoot;
        TestOverrides.ResolveDeletedTestFilesForTests = _ => [];
        try
        {
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
            {
                calls.Add((args, worktree == mainRoot ? "main" : "candidate"));
                if (args.Contains("--list-tests"))
                {
                    if (fault == "discovery-io") throw new IOException("trusted main discovery fixture IO failure");
                    if (fault == "discovery-failed") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "discovery failed"));
                    if (fault == "discovery-timeout") return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "discovery timed out", TimedOut: true));
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        """{"schemaVersion":1,"tests":[{"displayName":"Removed.Tests.One"},{"displayName":"Removed.Tests.Two"},{"displayName":"Removed.Tests.Three"}]}"""));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(fault == "build-failed" ? 1 : 0,
                    fault == "build-failed" ? "error CS1002: fixture build failed" : "Build succeeded."));
            });
            var result = await verifier.RunStructuralCoverageForTests(root, goal, [Project], approved, []);
            return (result, calls);
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal);
            DeleteDirectoryWithRetry(root);
            DeleteDirectoryWithRetry(mainRoot);
        }
    }

    private static void WriteManifest(string root, IEnumerable<string> checks, IEnumerable<string> invocations, string templateRoot)
    {
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), JsonSerializer.Serialize(new
        {
            version = 1,
            engine = new
            {
                enforceStructuralCoverage = true,
                mtpInvocations = invocations.Select(project => new
                {
                    project, executablePathTemplate = templateRoot + "/{projectName}/{configuration}/{projectName}{executableExtension}",
                    arguments = new[] { "{executable}" }
                }).ToArray()
            },
            checks = checks.Select(project => new { name = "removed check", type = "dotnet-test", project, runner = "mtp" }).ToArray(),
            forbiddenChangedPathGlobs = Array.Empty<string>()
        }));
    }
}
