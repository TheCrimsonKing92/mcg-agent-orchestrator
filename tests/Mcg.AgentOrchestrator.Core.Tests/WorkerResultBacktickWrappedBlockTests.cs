using Mcg.AgentOrchestrator.Core;

public sealed class WorkerResultBacktickWrappedBlockTests
{
    private const string ObservedBlock = """
        The focused test-lane refactor is complete.

        `WORKER_RESULT:`
        `files: config/acceptance-manifest.json, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/LauncherScriptTests.cs, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpTestRunnerScriptTests.cs, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpNoBuildReceiptIdentityTests.cs, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProcessSpawningCollectionSplitTests.cs, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OrchestratorSnapshotStatusTimeoutTests.cs, tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MtpManagedFilterDiscoveryTests.cs`
        `commands: PowerShell source-and-manifest assertions (pass); git diff --check (exit 0); git status --short`
        `tests: deferred - LauncherScriptTests, MtpTestRunnerScriptTests, OrchestratorSnapshotStatusTimeoutTests, MtpManagedFilterDiscoveryTests, MtpNoBuildReceiptIdentityTests, ProcessSpawningCollectionSplitTests, AcceptanceLaneMembershipTests, AcceptanceLaneMembershipTestsProcessSpawningSplit, AcceptanceGateEngineSettingsTests`
        `commit: none`
        `blockers: none`
        `assigned_scope_complete: true`
        `model_fit: OpenAI/gpt-6-sol - adequate - focused test-lane refactor - traced shared helpers and verified moved source against HEAD`
        `skills: dotnet-windows-build-hygiene, verification-before-completion`
        `confidence: medium`
        `END_WORKER_RESULT`
        """;

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ObservedBlockHasValidAssignedScopeAndNoMalformedOutcome(bool useCrLf)
    {
        var output = useCrLf
            ? ObservedBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            : ObservedBlock;

        Xunit.Assert.True(WorkerResultBlockers.TryGetAssignedScopeComplete(output, out var value, out var diagnostic), diagnostic);
        Xunit.Assert.True(value);
        Xunit.Assert.Null(diagnostic);
        Xunit.Assert.False(WorkerResultBlockers.TryFindMalformedEvidenceBoundOutcome(output, out var malformedDiagnostic), malformedDiagnostic);
    }

    [Xunit.Fact]
    public void WrappedObservedBlockClassifiesLikeUnwrappedObservedBlock()
    {
        var unwrapped = ObservedBlock.Replace("`", string.Empty, StringComparison.Ordinal);
        var task = new TaskSpec(TaskId.New(), "Implement the focused test-lane refactor", AgentRole.Developer);

        var wrappedOutcome = DispatchFailureClassifier.Classify(task, Verification(ObservedBlock));
        var unwrappedOutcome = DispatchFailureClassifier.Classify(task, Verification(unwrapped));
        var wrappedRule = TaskOutcomeClassifier.TryExtractRule(wrappedOutcome.ClassifierReceipt);
        var unwrappedRule = TaskOutcomeClassifier.TryExtractRule(unwrappedOutcome.ClassifierReceipt);

        Xunit.Assert.False(string.IsNullOrWhiteSpace(wrappedRule));
        Xunit.Assert.NotEqual("unknown-failure", wrappedRule);
        Xunit.Assert.Equal(unwrappedRule, wrappedRule);
    }

    [Xunit.Theory]
    [Xunit.InlineData("commands: `git diff --check` (exit 0)")]
    [Xunit.InlineData("`files: a")]
    [Xunit.InlineData("files: a`")]
    [Xunit.InlineData("```")]
    [Xunit.InlineData("``")]
    public void UnwrapLeavesOtherBacktickShapesUnchanged(string line)
    {
        Xunit.Assert.Same(line, WorkerResultLineUnwrap.Unwrap(line));
    }

    [Xunit.Fact]
    public void WrappedInvalidAssignedScopeRemainsMalformed()
    {
        var output = ObservedBlock.Replace("`assigned_scope_complete: true`", "`assigned_scope_complete: yes`", StringComparison.Ordinal);

        Xunit.Assert.False(WorkerResultBlockers.TryGetAssignedScopeComplete(output, out _, out var diagnostic));
        Xunit.Assert.Equal("assigned_scope_complete requires true or false.", diagnostic);
        Xunit.Assert.True(WorkerResultBlockers.TryFindMalformedEvidenceBoundOutcome(output, out var malformedDiagnostic));
        Xunit.Assert.Equal(diagnostic, malformedDiagnostic);
    }

    private static TaskVerificationRecord Verification(string output)
    {
        Xunit.Assert.True(WorkerResultBlockers.TryGetAssignedScopeComplete(output, out var complete, out var diagnostic), diagnostic);
        return new TaskVerificationRecord(
            "codex exec", "C:\\repo", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true,
            HasCommittedChanges: true,
            AssignedScopeComplete: complete);
    }
}
