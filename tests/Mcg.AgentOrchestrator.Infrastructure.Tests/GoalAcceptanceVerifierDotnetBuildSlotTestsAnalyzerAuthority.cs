using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Serialized with JobAccounting: the gate fact owns stable slot 0; focused roots are private.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsAnalyzerAuthority
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task FocusedEvidence_BothArmsBuildWithoutAnalyzers()
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        var builds = new ConcurrentQueue<(string[] Arguments, string Worktree)>();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            if (args is ["dotnet", "build", ..])
                builds.Enqueue((args, worktree));
            if (Array.IndexOf(args, "--results-directory") >= 0)
                WriteMtpTrx(args, executedTestCount: 1, ["PreExistingProbeTests.ExistingBehavior"]);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var goalId = GoalId.New();
        try
        {
            var result = await verifier.RunFocusedEvidenceAsync(fixture.Root, goalId,
                "Core.Tests: PreExistingProbeTests", runBaselineArm: true);

            Assert.True(result.Accepted, result.Summary);
            Assert.True(result.Passed, result.Summary);
            Assert.Equal(FindingEvidenceArmDisposition.Green,
                Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline).Disposition);
            var baselineRoot = Path.Combine(Path.GetTempPath(), "mcg-focused-evidence-baselines")
                + Path.DirectorySeparatorChar;
            Assert.Contains(builds, build => build.Worktree.StartsWith(baselineRoot,
                StringComparison.OrdinalIgnoreCase));
            Assert.Contains(builds, build => Path.GetFullPath(build.Worktree).Equals(
                Path.GetFullPath(fixture.Root), StringComparison.OrdinalIgnoreCase));
            Assert.All(builds, build =>
            {
                foreach (var property in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
                    Assert.Contains(property, build.Arguments);
            });
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact]
    public async Task FullGate_StableSlotBuildsKeepAnalyzerAuthority()
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var manifestPath = Path.Combine(fixture.Root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["checks"] = new JsonArray(JsonNode.Parse("""
            { "name": "core tests", "type": "dotnet-test",
              "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" }
            """));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var builds = new ConcurrentQueue<string[]>();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, _, _) =>
        {
            if (args is ["dotnet", "build", ..])
                builds.Enqueue(args);
            if (Array.IndexOf(args, "--results-directory") >= 0)
                WriteMtpTrx(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
        });
        try
        {
            var result = await verifier.RunAsync(fixture.Root, GoalId.New(),
                changedFiles: ["Directory.Build.props"], stableSlotIndex: 0);

            Assert.True(result.Passed);
            Assert.NotEmpty(builds);
            Assert.All(builds, arguments => Assert.DoesNotContain(arguments, argument =>
                argument.Contains("RunAnalyzers", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("EnforceCodeStyleInBuild", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
        }
    }

    [Xunit.Fact]
    public void WithoutAnalyzers_PreservesArgumentOrderAndCachePlanIdentity()
    {
        var cachePlan = new DotnetBaseBuildCachePlan("main-sha", ["restore.csproj"],
            ["build.csproj"], ["cache.csproj"]);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", "Fixture.csproj"], cachePlan);

        var result = AcceptanceDotnetBuildPhase.WithoutAnalyzers(phase);

        Assert.NotSame(phase, result);
        Assert.Same(cachePlan, result.CachePlan);
        Assert.Equal(phase.BuildArguments.Concat(AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties),
            result.BuildArguments);
    }

    [Xunit.Fact]
    public void WithoutAnalyzers_ExistingPropertyIsNotDuplicated()
    {
        foreach (var existing in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
        {
            var phase = new DotnetTestBuildPhase(["dotnet", "build", existing], null);

            var result = AcceptanceDotnetBuildPhase.WithoutAnalyzers(phase);

            Assert.Equal(phase.BuildArguments, result.BuildArguments.Take(phase.BuildArguments.Length));
            foreach (var property in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
                Assert.Single(result.BuildArguments, argument => argument == property);
            Assert.Equal(result.BuildArguments,
                AcceptanceDotnetBuildPhase.WithoutAnalyzers(result).BuildArguments);
        }
    }

    [Xunit.Fact]
    public void WithoutAnalyzers_ConflictingArgumentsAndInputRemainUnchanged()
    {
        var existing = AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties[0];
        var conflicting = existing.Replace("=false", "=true", StringComparison.Ordinal);
        var phase = new DotnetTestBuildPhase(["dotnet", "build", conflicting, existing, existing], null);
        var snapshot = phase.BuildArguments.ToArray();

        var result = AcceptanceDotnetBuildPhase.WithoutAnalyzers(phase);

        Assert.Equal(snapshot, phase.BuildArguments);
        Assert.NotSame(phase.BuildArguments, result.BuildArguments);
        Assert.Equal(snapshot, result.BuildArguments.Take(snapshot.Length));
        Assert.Equal(2, result.BuildArguments.Count(argument => argument == existing));
        Assert.Equal(AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties[1], result.BuildArguments[^1]);

        var conflictOnly = new DotnetTestBuildPhase(["dotnet", "build", conflicting], null);
        Assert.Equal(conflictOnly.BuildArguments.Concat(AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties),
            AcceptanceDotnetBuildPhase.WithoutAnalyzers(conflictOnly).BuildArguments);
    }
}
