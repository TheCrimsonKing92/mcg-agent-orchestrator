using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFirstFocusedEvidenceScenarioTests : RemoteFirstFocusedEvidenceFixture
{
    [Fact]
    public async Task OwnedRun_RemoteAccepted_UsesBoundReceiptWithoutLocalBuildOrTest()
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        var check = Assert.Single(result.Checks);
        Assert.True(check.Passed);
        Assert.Equal("remote-executor=fixture-executor", check.ResultSummary);
        Assert.Equal(3, check.ExecutedTestCount);
        Assert.NotEmpty(check.TestResultPaths!);
        Assert.All(check.TestResultPaths!, path =>
        {
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.GetDirectoryName(scenario.Prefix)!, path);
            Assert.Contains("remote-first", path);
        });
        var request = Assert.Single(fake.Requests);
        Assert.Equal(scenario.Head, request.VerifyingCommitSha);
        Assert.Equal(scenario.Tree, request.CandidateTreeSha);
        Assert.Equal(scenario.Head, request.MainSha);
        Assert.Equal("selected-attempt", request.AttemptId);
        Assert.Equal(check.TestResultAttemptId, request.AttemptId);
        Assert.Equal("FullyQualifiedName~AlphaTests", request.Filter);
        Assert.Equal(GoalAcceptanceVerifier.ShortHash(request.Filter), request.FilterHash);
        Assert.Equal(RemoteFocusedEvidenceShadow.ManifestIdentity(Project, request.Filter, "mtp"), request.ManifestIdentity);
        Assert.Equal(0, scenario.LocalCalls);
        Assert.Equal(0, scenario.BuildCalls);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Null(row.EvidenceShadow);
        Assert.Equal(row.Expected, row.Observed);
    }

    [Fact]
    public async Task OwnedRun_RemoteRed_LocalPassingRunnerCannotOverruleFailure()
    {
        using var scenario = CreateScenario();
        scenario.CompletedExecutor(red: true);
        var result = await scenario.RunAsync();
        Assert.False(result.Passed);
        var check = Assert.Single(result.Checks);
        Assert.False(check.Passed);
        Assert.Equal(["AlphaTests.RemoteFailure"], check.FailingTestIdentities);
        Assert.Contains("AlphaTests.RemoteFailure", check.ResultSummary);
        Assert.All(check.TestResultPaths!, path => Assert.True(File.Exists(path)));
        Assert.Equal(0, scenario.LocalCalls);
        Assert.Equal(RemoteLaneOutcomeCode.RemoteRed, Assert.Single(scenario.Health()).Outcome);
    }

    [Theory]
    [InlineData(false, "Unreachable")]
    [InlineData(true, "BindingMismatchCommit")]
    public async Task OwnedRun_RemoteFault_UsesLocalVerdict(bool wrongCommit, string outcome)
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor(change: result => result with { VerifyingCommitSha = "wrong" });
        if (!wrongCommit) fake.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, "fixture-unreachable"));
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(1, scenario.LocalCalls);
        Assert.DoesNotContain("remote-executor=", Assert.Single(result.Checks).ResultSummary ?? "");
        var row = Assert.Single(scenario.Health());
        Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(outcome), row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
    }

    [Theory]
    [InlineData("shadow")]
    [InlineData("off")]
    public async Task OwnedRun_ExistingMode_PreservesEveryControlField(string mode)
    {
        using var scenario = CreateScenario();
        scenario.Configure(mode, sample: 1);
        var fake = scenario.CompletedExecutor();
        var observed = await scenario.RunAsync();
        var rows = scenario.Health();
        if (mode == "off") { Assert.Empty(rows); Assert.Empty(fake.Requests); }
        else
        {
            Assert.NotEmpty(fake.Requests);
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.True(row.EvidenceShadow));
        }
        Assert.All(rows, row => Assert.DoesNotContain("remote-first-fallback", row.Reason ?? ""));
        scenario.Configure("off");
        var control = await scenario.RunAsync("control-attempt");
        Assert.Equal(control.Request, observed.Request);
        Assert.Equal(control.Passed, observed.Passed);
        Assert.Equal(control.Accepted, observed.Accepted);
        Assert.Equal(control.OutcomeReason, observed.OutcomeReason);
        Assert.Equal(control.Rejection, observed.Rejection);
        Assert.Equal(NormalizeSummary(control), NormalizeSummary(observed));
        Assert.Equal(control.Checks.Count, observed.Checks.Count);
        foreach (var (expected, actual) in control.Checks.Zip(observed.Checks))
        {
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Passed, actual.Passed);
            Assert.Equal(expected.ExitCode, actual.ExitCode);
            Assert.Equal(expected.ExecutedTestCount, actual.ExecutedTestCount);
            Assert.Equal(expected.OutputTail, actual.OutputTail);
            Assert.Equal(expected.ResultSummary, actual.ResultSummary);
            Assert.Equal(expected.FailureClassification, actual.FailureClassification);
            Assert.Equal(expected.TestResultPaths, actual.TestResultPaths);
            Assert.Equal(expected.FailingTestIdentities, actual.FailingTestIdentities);
        }
    }

    [Fact]
    public async Task ResourceGuard_ForeignKey_UsesLocalVerdictWithoutSubmission()
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();
        // The focused resolver does not assign ExclusiveResourceKeys. Exercise its
        // defensive guard through the entry point with the same real owner and workspace.
        var result = await scenario.DirectAsync([FocusedCheck(keys: ["foreign-key"])]);
        Assert.True(Assert.Single(result.Results).Passed);
        Assert.Equal(1, scenario.LocalCalls);
        Assert.Empty(fake.Requests);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
    }

    [Fact]
    public async Task OwnedRun_ZeroExecutedTrx_FallsBackRatherThanPassingRemote()
    {
        using var scenario = CreateScenario();
        scenario.CompletedExecutor(executed: 0);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(1, scenario.LocalCalls);
        Assert.Equal(1, Assert.Single(result.Checks).ExecutedTestCount);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.TrxIncomplete, row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
    }

    [Fact]
    public async Task OwnedRun_DirtyWorktree_RecordsLocalFallbackWithoutBlamingExecutor()
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();
        File.WriteAllText(Path.Combine(scenario.Root, "dirty.txt"), "uncommitted");
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(1, scenario.LocalCalls);
        Assert.Empty(fake.Requests);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, row.Outcome);
        Assert.Equal("remote-first-fallback: dirty-worktree", row.Reason);
        Assert.Empty(row.ExecutorId);
        Assert.Null(row.FaultOwner);
        Assert.Single(scenario.Progress.Where(line => line.Contains("dirty-worktree", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(26, true)]
    [InlineData(24, true)]
    [InlineData(1, false)]
    public async Task OwnedRun_RemoteRedWithoutNamedFailure_UsesLocalVerdict(int exit, bool missing)
    {
        using var scenario = CreateScenario();
        scenario.CompletedExecutor(change: result => result with { ExitCode = exit, TestResultPaths = missing ? [] : result.TestResultPaths });
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(1, scenario.LocalCalls);
        Assert.DoesNotContain("remote-executor=", Assert.Single(result.Checks).ResultSummary ?? "");
        Assert.StartsWith("remote-first-fallback", Assert.Single(scenario.Health()).Reason);
    }

    private static string NormalizeSummary(FocusedEvidenceRunResult result)
    {
        var summary = result.Summary;
        foreach (var check in result.Checks)
            if (!string.IsNullOrEmpty(check.ArtifactsPath)) summary = summary.Replace(check.ArtifactsPath, "<local-build-artifacts>", StringComparison.Ordinal);
        return summary;
    }
}
