using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json.Nodes;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedEvidenceBuildPhaseTimeout
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(1, 25)]
    [Xunit.InlineData(4, 30)]
    public async Task SlowInBoundBuild_ReachesTestsWithFullFocusedTimeout(int targetCount, int buildMinutes)
    {
        var (result, calls) = await RunBuildScenarioAsync(
            TimeSpan.FromMinutes(15), buildMinutes, targetCount);

        Assert.True(result.Accepted);
        Assert.True(result.Passed, result.Summary);
        Assert.True(Assert.Single(result.Checks).Passed);
        Assert.Equal(FindingEvidenceArmDisposition.Green, Assert.Single(result.Arms!).Disposition);
        Assert.Equal(TimeSpan.FromMinutes(buildMinutes), Assert.Single(calls.Where(call => call.Build)).Timeout);
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(calls.Where(call => call.Test)).Timeout);
        Assert.True(calls.FindIndex(call => call.Build) < calls.FindIndex(call => call.Test));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task BuildExceedingOwnBound_IsApparatusFailureAndNeverLaunchesTests(bool vstest)
    {
        var (result, calls) = await RunBuildScenarioAsync(
            TimeSpan.FromMinutes(35), vstest: vstest);

        Assert.True(result.Accepted);
        Assert.False(result.Passed);
        var check = Assert.Single(result.Checks);
        Assert.False(check.Passed);
        Assert.Equal(-1, check.ExitCode);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(calls.Where(call => call.Build)).Timeout);
        Assert.DoesNotContain(calls, call => call.Test);
        Assert.Equal(AcceptanceFailureClassifications.BuildPhaseTimeout, check.FailureClassification);
        Assert.True(AcceptanceFailureClassifications.IsEnvironmentalApparatus(check.FailureClassification));
        Assert.True(check.CompletionDecision!.TimedOut);
        Assert.Contains("build phase timed out (build-phase budget)", check.ResultSummary);
        Assert.Contains("elapsed=35m budget=30m", check.ResultSummary);
        Assert.DoesNotContain("acceptance-check-timeout", check.Name);
        Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, Assert.Single(result.Arms!).Disposition);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, result.OutcomeReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task CompileOrCaptureFailure_DoesNotBecomeBuildTimeout(bool captureLimited)
    {
        var failure = captureLimited
            ? new GoalAcceptanceVerifier.CommandResult(-1, "capture stopped",
                CaptureLimited: true, CaptureLimitBytes: 1024)
            : new GoalAcceptanceVerifier.CommandResult(1, "error CS1002: ; expected");
        var (result, calls) = await RunBuildScenarioAsync(TimeSpan.Zero, buildFailure: failure);

        var check = Assert.Single(result.Checks);
        Assert.False(check.Passed);
        Assert.Equal(captureLimited
            ? AcceptanceShardCompletionPredicates.TimedOut
            : AcceptanceShardCompletionPredicates.NonzeroExit, check.FailureClassification);
        Assert.NotEqual(AcceptanceFailureClassifications.BuildPhaseTimeout, check.FailureClassification);
        Assert.False(AcceptanceFailureClassifications.IsEnvironmentalApparatus(check.FailureClassification));
        Assert.Contains("build phase failed", check.ResultSummary);
        Assert.DoesNotContain("build phase timed out", check.ResultSummary);
        Assert.DoesNotContain(calls, call => call.Test);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, Assert.Single(result.Arms!).Disposition);
        Assert.Null(result.OutcomeReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, 20)]
    [Xunit.InlineData(0, 20)]
    [Xunit.InlineData(-1, 20)]
    [Xunit.InlineData(30, 30)]
    public void BuildTimeout_UsesSettingsAndPreservesLongerCheckBounds(int? configuredMinutes, int expectedMinutes)
    {
        var settings = AcceptanceGateEngineSettings.Parse($$"""
            { "engine": { "timeouts": { "defaultMinutes": 40, "buildPhaseMinutes": {{configuredMinutes?.ToString() ?? "null"}} } } }
            """);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), settings.ResolveBuildPhaseTimeout(10));
        Assert.Equal(TimeSpan.FromMinutes(40), settings.ResolveBuildPhaseTimeout(null));
        Assert.Equal(TimeSpan.FromMinutes(45), settings.ResolveBuildPhaseTimeout(45));
        Assert.Equal(TimeSpan.FromMinutes(10), settings.ResolveCheckTimeout(10));
    }

    private static async Task<(FocusedEvidenceRunResult Result, List<CommandCall> Calls)> RunBuildScenarioAsync(
        TimeSpan buildElapsed,
        int buildMinutes = 30,
        int targetCount = 1,
        bool vstest = false,
        GoalAcceptanceVerifier.CommandResult? buildFailure = null)
    {
        var root = CreateManifestWorkspace("""
            { "version": 1, "checks": [], "forbiddenChangedPathGlobs": [] }
            """);
        var goalId = new GoalId(Guid.NewGuid().ToString("N"));
        try
        {
            var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["engine"]!["timeouts"]!["defaultMinutes"] = 40;
            manifest["engine"]!["timeouts"]!["buildPhaseMinutes"] = buildMinutes;
            if (vstest)
            {
                manifest["checks"] = JsonNode.Parse("""
                    [{ "name": "infrastructure tests", "type": "dotnet-test", "runner": "vstest",
                       "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                       "timeoutMinutes": 10 }]
                    """);
            }
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var calls = new List<CommandCall>();
            var verifier = new GoalAcceptanceVerifier((args, _, timeout, _) =>
            {
                var isBuild = args.Contains("build", StringComparer.OrdinalIgnoreCase);
                var isTest = IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") ||
                    args.Contains("test", StringComparer.OrdinalIgnoreCase);
                calls.Add(new CommandCall(isBuild, isTest, timeout));
                if (isBuild)
                {
                    if (buildFailure is not null)
                    {
                        return Task.FromResult(buildFailure);
                    }

                    // Simulate elapsed work without making the test wait on the clock.
                    var timedOut = buildElapsed > timeout;
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        timedOut ? -1 : 0, timedOut ? "build interrupted" : "Build succeeded.",
                        TimedOut: timedOut, Timeout: timeout, Elapsed: buildElapsed));
                }

                if (isTest)
                {
                    WriteMtpTrx(args);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                        0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
                }

                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, string.Empty));
            });
            var targets = Enumerable.Range(1, targetCount).Select(index => $"BuildTimeoutTarget{index}Tests");
            var result = await verifier.RunFocusedEvidenceAsync(root, goalId,
                $"Infrastructure.Tests: {string.Join(',', targets)}");
            return (result, calls);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
            DeleteDirectoryWithRetry(root);
        }
    }

    private sealed record CommandCall(bool Build, bool Test, TimeSpan Timeout);
}
