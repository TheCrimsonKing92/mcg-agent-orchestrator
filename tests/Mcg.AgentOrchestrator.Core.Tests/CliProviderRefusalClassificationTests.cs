using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each fixture owns an in-memory kernel and fixed completion time.
public sealed class CliProviderRefusalClassificationTests
{
    [Theory]
    [InlineData("You've hit your weekly limit \u00B7 resets Oct 7, 12pm (America/Chicago)", "2026-10-05T10:12:56Z", "2026-10-07T17:00:00Z")]
    [InlineData("You've hit your weekly limit \u00B7 resets Sep 30, 12pm (America/Chicago)", "2026-09-29T11:48:46Z", "2026-09-30T17:00:00Z")]
    [InlineData("You've hit your weekly limit \u00B7 resets Jan 2, 12pm (America/Chicago)", "2026-12-31T20:00:00Z", "2027-01-02T18:00:00Z")]
    [InlineData("You've hit your session limit \u00B7 resets Oct 7, 12am (America/Chicago)", "2026-10-05T10:12:56Z", "2026-10-07T05:00:00Z")]
    [InlineData("You've hit your daily limit \u00B7 resets Oct 7, 1:15pm (America/Chicago)", "2026-10-05T10:12:56Z", "2026-10-07T18:15:00Z")]
    public void ClaudeLimitUsesAbsoluteReset(string stdout, string completedAt, string expectedReset)
    {
        var verification = Verification(stdout, completedAt: completedAt);

        var outcome = DispatchFailureClassifier.Classify(CliTask(), verification);

        Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Assert.True(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(verification, out var reset));
        Assert.Equal(ParseUtc(expectedReset), reset);
    }

    [Theory]
    [InlineData("You've hit your weekly limit \u00B7 resets someday")]
    [InlineData("You've hit your weekly limit \u00B7 resets Feb 30, 12pm (America/Chicago)")]
    [InlineData("You've hit your weekly limit \u00B7 resets Oct 7, 12pm (Unknown/Zone)")]
    [InlineData("You've hit your weekly limit \u00B7 resets Oct 7, 0pm (America/Chicago)")]
    [InlineData("You've hit your weekly limit \u00B7 resets Oct 7, 13pm (America/Chicago)")]
    [InlineData("You've hit your weekly limit \u00B7 resets Oct 7, 12:60pm (America/Chicago)")]
    public void UnparseableResetKeepsLimitWithoutRetryTime(string stdout)
    {
        var verification = Verification(stdout);

        Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit,
            DispatchFailureClassifier.Classify(CliTask(), verification).Kind);
        Assert.False(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(verification, out _));
    }

    [Fact]
    public void ResetInDstGapKeepsLimitWithoutRetryTime()
    {
        var verification = Verification(
            "You've hit your weekly limit \u00B7 resets Mar 8, 2:30am (America/Chicago)",
            completedAt: "2026-03-01T10:00:00Z");

        Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit,
            DispatchFailureClassifier.Classify(CliTask(), verification).Kind);
        Assert.False(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(verification, out _));
    }

    [Fact]
    public void GrokSignInRefusalNamesItsLoginCommand()
    {
        const string stderr = "OIDC: token refresh HTTP error http_status=400 oauth2_error=Some(\"invalid_grant\")\n" +
            "auth.refresh.permanent_failure reason=RefreshTokenRejected\n" +
            "Error: Not signed in. To authenticate without a browser, run: grok login --device-code";
        var task = CliTask("grok-cli", ProviderKind.XaiGrokCli, AgentRole.Tester);

        var outcome = DispatchFailureClassifier.Classify(task, Verification(string.Empty, stderr));

        Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("remediation=run 'grok login --device-code'", outcome.EvidenceSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("codex login", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeOAuthExpiryIsAuthenticationRefusal()
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(
            "Failed to authenticate: OAuth session expired and could not be refreshed"));

        Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("remediation=provider re-login for claude-cli", outcome.EvidenceSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("codex login", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")]
    [InlineData("API Error 529 Overloaded")]
    [InlineData("API Error: 529")]
    [InlineData("API Error: 503 Overloaded")]
    [InlineData("API Error: 503 overloaded_error")]
    public void ClaudeOverloadUsesConnectivityRecovery(string stdout)
    {
        var outcome = DispatchFailureClassifier.Classify(CliTask(), Verification(stdout));

        Assert.Equal(DispatchOutcomeKind.ProviderConnectivity, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    [Theory]
    [InlineData("API Error: 429 rate_limit_error")]
    [InlineData("API Error: 429 Overloaded")]
    public void Claude429KeepsLimitClassification(string stdout)
    {
        Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit,
            DispatchFailureClassifier.Classify(CliTask(), Verification(stdout)).Kind);
    }

    [Theory]
    [InlineData("The fixture mentions You've hit your weekly limit.")]
    [InlineData("The fixture mentions API Error 529 Overloaded.")]
    public void WorkerResultAndProseAreNotProviderRefusals(string prose)
    {
        var stdout = "WORKER_RESULT:\nfiles: none\ncommands: none\ntests: deferred - FixtureTests\n" +
            "commit: none\nblockers: none\nassigned_scope_complete: true\n" +
            "model_fit: OpenAI/test - adequate - fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT\n" + prose;

        AssertNotProviderOutcome(DispatchFailureClassifier.Classify(CliTask(), Verification(stdout)));
    }

    [Theory]
    [InlineData("You've hit your weekly limit \u00B7 resets Oct 7, 12pm (America/Chicago)")]
    [InlineData("Failed to authenticate: OAuth session expired and could not be refreshed")]
    [InlineData("API Error 529 Overloaded")]
    public void RefusalFollowedByWorkerOutputIsNotProviderRefusal(string refusal)
    {
        AssertNotProviderOutcome(DispatchFailureClassifier.Classify(CliTask(),
            Verification(refusal + "\nInspected the fixture and completed the requested work.")));
    }

    [Theory]
    [InlineData("You've hit your weekly limit")]
    [InlineData("Failed to authenticate: OAuth session expired and could not be refreshed")]
    [InlineData("API Error 529 Overloaded")]
    public void SuccessfulExitDoesNotAdmitBareRefusal(string stdout)
    {
        AssertNotProviderOutcome(DispatchFailureClassifier.Classify(CliTask(), Verification(stdout, exitCode: 0)));
    }

    [Theory]
    [InlineData(" You've hit your weekly limit")]
    [InlineData("\tAPI Error 529 Overloaded")]
    [InlineData("fixture.cs:12: API Error 529 Overloaded")]
    [InlineData("API error (status 402 Payment Required): request rejected")]
    public void IndentationSourceEchoAndOtherApiShapesAreNotRefusals(string stdout)
    {
        AssertNotProviderOutcome(DispatchFailureClassifier.Classify(CliTask(), Verification(stdout)));
    }

    [Fact]
    public void CodexDiagnosticKeepsExistingLimitAndRetryTime()
    {
        var verification = Verification(string.Empty,
            "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.");

        Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit,
            DispatchFailureClassifier.Classify(CliTask("codex-cli", ProviderKind.OpenAICodexCli), verification).Kind);
        Assert.True(DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(verification, out var reset));
        Assert.Equal(ParseUtc("2026-10-05T16:58:00Z"), reset);
    }

    private static TaskSpec CliTask(
        string workerName = "claude-cli",
        ProviderKind providerKind = ProviderKind.AnthropicClaudeCli,
        AgentRole role = AgentRole.Reviewer)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify CLI refusal", [new TaskSpec(TaskId.New(), "Inspect evidence", role)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = Assert.Single(goal.Tasks);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            workerName, "opaque command", "C:\\repo", clock.UtcNow, WorkerProviderKind: providerKind));
        return task;
    }

    private static TaskVerificationRecord Verification(
        string stdout,
        string stderr = "",
        int exitCode = 1,
        string completedAt = "2026-10-05T10:12:56Z") =>
        new("opaque command", "C:\\repo", exitCode, stdout, stderr, ParseUtc(completedAt));

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static void AssertNotProviderOutcome(DispatchOutcome outcome)
    {
        Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
        Assert.NotEqual(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
        Assert.NotEqual(DispatchOutcomeKind.ProviderConnectivity, outcome.Kind);
    }
}
