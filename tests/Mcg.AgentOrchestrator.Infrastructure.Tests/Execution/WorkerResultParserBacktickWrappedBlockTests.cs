using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerResultParserBacktickWrappedBlockTests
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
    public void ObservedBlockFieldsMatchUnwrappedBlock(bool useCrLf)
    {
        var wrapped = useCrLf
            ? ObservedBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal)
            : ObservedBlock;
        var unwrapped = wrapped.Replace("`", string.Empty, StringComparison.Ordinal);

        Xunit.Assert.True(WorkerResultParser.TryParseFields(wrapped, out var wrappedFields, out var wrappedDiagnostic), wrappedDiagnostic);
        Xunit.Assert.True(WorkerResultParser.TryParseFields(unwrapped, out var unwrappedFields, out var unwrappedDiagnostic), unwrappedDiagnostic);
        Xunit.Assert.Equal(unwrappedFields.OrderBy(pair => pair.Key), wrappedFields.OrderBy(pair => pair.Key));
        Xunit.Assert.Equal("none", wrappedFields["commit"]);
        Xunit.Assert.EndsWith("MtpManagedFilterDiscoveryTests.cs", wrappedFields["files"].Split(',')[^1], StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void NoOpenerFieldScanUnwrapsWholeFieldLines()
    {
        var wrapped = ObservedBlock.Replace("`WORKER_RESULT:`", string.Empty, StringComparison.Ordinal)
            .Replace("`END_WORKER_RESULT`", string.Empty, StringComparison.Ordinal);
        var unwrapped = wrapped.Replace("`", string.Empty, StringComparison.Ordinal);

        Xunit.Assert.True(WorkerResultParser.TryParseFields(wrapped, out var wrappedFields, out var wrappedDiagnostic), wrappedDiagnostic);
        Xunit.Assert.True(WorkerResultParser.TryParseFields(unwrapped, out var unwrappedFields, out var unwrappedDiagnostic), unwrappedDiagnostic);
        Xunit.Assert.Equal(unwrappedFields.OrderBy(pair => pair.Key), wrappedFields.OrderBy(pair => pair.Key));
    }
}
