using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

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
    public async Task FullGate_StableSlotBuildsRunWithoutAnalyzers()
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "Mcg.AgentOrchestrator.sln"), string.Empty);
        var manifestPath = Path.Combine(fixture.Root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["checks"] = new JsonArray(JsonNode.Parse("""
            { "name": "core tests", "type": "dotnet-test", "runner": "mtp",
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

            Assert.True(result.Passed, result.OutputTail);
            Assert.NotEmpty(builds);
            Assert.All(builds, arguments =>
            {
                foreach (var property in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
                    Assert.Single(arguments, argument => argument == property);
            });
        }
        finally
        {
            TryDeleteStableSlotHeartbeat(0);
        }
    }

    [Xunit.Fact]
    public async Task FullGate_NoStableSlotBuildsRunWithoutAnalyzers()
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        var manifestPath = Path.Combine(fixture.Root, "config", "acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["checks"] = new JsonArray(JsonNode.Parse("""
            { "name": "core tests", "type": "dotnet-test", "runner": "mtp",
              "project": "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj" }
            """));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var builds = new ConcurrentQueue<(string[] Arguments, string Worktree)>();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            if (args is ["dotnet", "build", ..])
                builds.Enqueue((args, worktree));
            if (Array.IndexOf(args, "--results-directory") >= 0)
                WriteMtpTrx(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
        });
        var goalId = GoalId.New();
        try
        {
            var result = await verifier.RunAsync(fixture.Root, goalId,
                changedFiles: ["Directory.Build.props"]);

            Assert.True(result.Passed, result.OutputTail);
            var candidateBuilds = builds.Where(build => build.Worktree == fixture.Root).ToArray();
            Assert.NotEmpty(candidateBuilds);
            Assert.All(candidateBuilds, build =>
            {
                foreach (var property in AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties)
                    Assert.Single(build.Arguments, argument => argument == property);
            });
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    [Xunit.Fact]
    public void Create_EveryGateShapeEndsWithAnalyzersOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-gate-shapes", Guid.NewGuid().ToString("N"));
        var withSolution = Path.Combine(root, "with-solution");
        var withoutSolution = Path.Combine(root, "without-solution");
        Directory.CreateDirectory(withSolution);
        Directory.CreateDirectory(withoutSolution);
        try
        {
            File.WriteAllText(Path.Combine(withSolution, "Mcg.AgentOrchestrator.sln"), string.Empty);
            GoalAcceptanceVerifier.AcceptanceManifestCheck[] solutionChecks =
                [new() { Type = "dotnet-test", Project = "Fixture.sln" }];
            GoalAcceptanceVerifier.AcceptanceManifestCheck[] projectChecks =
                [new() { Type = "dotnet-test", Project = "Fixture.csproj" }];
            GoalAcceptanceVerifier.AcceptanceManifestCheck[] commandChecks =
                [new() { Type = "command" }];
            var plan = PolicyShardPlan.NotApplicable("analyzer argument shapes");
            var phases = new[]
            {
                AcceptanceDotnetBuildPhase.Create(withoutSolution, solutionChecks, null, plan, _ => null),
                AcceptanceDotnetBuildPhase.Create(withSolution, projectChecks, null, plan, _ => null),
                AcceptanceDotnetBuildPhase.Create(withoutSolution, projectChecks, null, plan, _ => null),
                AcceptanceDotnetBuildPhase.Create(withoutSolution, commandChecks, null, plan, _ => null)
            };

            Assert.All(phases, phase =>
            {
                Assert.Equal(new[] { "dotnet", "build" }, phase.BuildArguments.Take(2));
                var properties = AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties;
                Assert.Equal(properties, phase.BuildArguments.TakeLast(properties.Count));
                foreach (var property in properties)
                    Assert.Single(phase.BuildArguments, argument => argument == property);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void BuildArguments_ExistingOffIsUniqueAndEarlierOnIsOverridden()
    {
        var properties = AcceptanceCheckCommandBuilder.AnalyzersOffBuildProperties;
        var existing = properties[0];
        var withExisting = AcceptanceCheckCommandBuilder.BuildDotnetTestBuildArguments(
            ["dotnet", "test", "Fixture.csproj", existing]);
        foreach (var property in properties)
            Assert.Single(withExisting, argument => argument == property);

        var conflicting = existing.Replace("=false", "=true", StringComparison.Ordinal);
        var withConflict = AcceptanceCheckCommandBuilder.BuildDotnetTestBuildArguments(
            ["dotnet", "test", "Fixture.csproj", conflicting]);
        Assert.Contains(conflicting, withConflict);
        Assert.Equal(properties, withConflict.TakeLast(properties.Count));
        Assert.Equal(existing, withConflict.Last(argument =>
            argument.StartsWith("-p:RunAnalyzers=", StringComparison.Ordinal)));
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
