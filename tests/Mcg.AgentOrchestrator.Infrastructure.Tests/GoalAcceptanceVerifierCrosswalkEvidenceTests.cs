using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierCrosswalkEvidenceTests
{
    [Theory]
    [InlineData("passed", true, null)]
    [InlineData("failed", false, CrosswalkDiagnosticCategory.TestFailures)]
    [InlineData("error", false, CrosswalkDiagnosticCategory.TestFailures)]
    [InlineData("skipped", false, CrosswalkDiagnosticCategory.NoExecution)]
    [InlineData("missing", false, CrosswalkDiagnosticCategory.MissingCoverage)]
    [InlineData("duplicate", false, CrosswalkDiagnosticCategory.MalformedResults)]
    [InlineData("malformed", false, CrosswalkDiagnosticCategory.MalformedResults)]
    [InlineData("json", false, CrosswalkDiagnosticCategory.InvalidJson)]
    [InlineData("extra-pass", true, null)]
    [InlineData("extra-fail", false, CrosswalkDiagnosticCategory.TestFailures)]
    public async Task Verdict_RequiresUsableExecutedCoverage(string kind, bool passed, CrosswalkDiagnosticCategory? category)
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        var id = CrosswalkAcceptanceFixture.Id;
        fixture.Output = kind switch
        {
            "missing" => CrosswalkAcceptanceFixture.Report(("other", "passed")),
            "duplicate" => CrosswalkAcceptanceFixture.Report((id, "passed"), (id, "passed")),
            "malformed" => "{}", "json" => "not JSON",
            "extra-pass" => CrosswalkAcceptanceFixture.Report((id, "passed"), ("other", "passed")),
            "extra-fail" => CrosswalkAcceptanceFixture.Report((id, "passed"), ("other", "failed")),
            _ => CrosswalkAcceptanceFixture.Report((id, kind))
        };
        var check = await fixture.RunAsync();
        Assert.Equal(passed, check.Passed);
        Assert.Equal(2, fixture.Calls.Count);
        if (category is not null) Assert.Contains(check.CrosswalkDiagnostics!, d => d.Category == category);
        if (kind is "duplicate" or "malformed" or "json") Assert.Empty(check.NormalizedTestResultPaths!);
        else
        {
            var path = Assert.Single(check.NormalizedTestResultPaths!);
            Assert.StartsWith(fixture.Prefix + ".", path);
            var read = new NormalizedTestResultSource().Read(path);
            Assert.Equal(TestResultReadStatus.Readable, read.Status);
            Assert.Equal(kind.StartsWith("extra-", StringComparison.Ordinal) ? 2 : 1, read.Records.Count);
        }
    }

    [Theory]
    [InlineData(1, CrosswalkDiagnosticCategory.NonzeroExit)]
    [InlineData(2, CrosswalkDiagnosticCategory.UnsupportedAdapter)]
    public async Task FailedInvocation_RetainsCurrentNormalizedEvidenceAndStderr(int exit, CrosswalkDiagnosticCategory category)
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        fixture.Exit = exit;
        fixture.Output = CrosswalkAcceptanceFixture.Report((CrosswalkAcceptanceFixture.Id, "failed"));
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(exit, check.ExitCode);
        Assert.Equal(category, check.CrosswalkDiagnostics![0].Category);
        Assert.Equal(fixture.Stderr + fixture.Stderr, check.ProcessStderr);
        Assert.Equal(CrosswalkAcceptanceFixture.Id, Assert.Single(check.FailingTestIdentities!));
        Assert.True(ReceiptTestFailureReader.TryReadNormalized(check, out var failures));
        Assert.Equal("assertion detail", Assert.Single(failures).Message);
        Assert.Null(check.TestResultPaths);
        var roundtrip = JsonSerializer.Deserialize<AcceptanceCheckResult>(JsonSerializer.Serialize(check))!;
        Assert.Equal(check.NormalizedTestResultPaths, roundtrip.NormalizedTestResultPaths);
        Assert.Equal(check.CrosswalkDiagnostics, roundtrip.CrosswalkDiagnostics);
        Assert.Equal(failures, ReceiptTestFailureReader.Read(roundtrip));
    }

    [Theory]
    [InlineData(2, CrosswalkDiagnosticCategory.UnsupportedAdapter)]
    [InlineData(9, CrosswalkDiagnosticCategory.NonzeroExit)]
    public async Task PrimaryInvocationFailure_IsNotOverwrittenByInvalidJson(int exit, CrosswalkDiagnosticCategory primary)
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        fixture.Exit = exit;
        fixture.Output = "invalid JSON";
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(primary, check.CrosswalkDiagnostics![0].Category);
        Assert.Contains(check.CrosswalkDiagnostics, d => d.Category == CrosswalkDiagnosticCategory.InvalidJson);
        Assert.Contains(fixture.Stderr, check.ProcessStderr);
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Theory]
    [InlineData(false, CrosswalkDiagnosticCategory.NotConfigured)]
    [InlineData(true, CrosswalkDiagnosticCategory.MissingBinary)]
    public async Task UnavailableTool_FailsWithoutLaunching(bool missing, CrosswalkDiagnosticCategory expected)
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        Environment.SetEnvironmentVariable(CrosswalkClient.PathVariable, missing ? fixture.Binary + ".missing" : null);
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(expected, Assert.Single(check.CrosswalkDiagnostics!).Category);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task StaleArtifactsAndPreviousAttempts_CannotSatisfyNewInvocation()
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        await using var firstOwner = fixture.Owner(id: "previous");
        var first = (await fixture.RunOwnedAsync(firstOwner)).Result;
        Assert.True(first.Passed);
        var oldPath = Assert.Single(first.NormalizedTestResultPaths!);
        fixture.Output = "{\"path\":" + JsonSerializer.Serialize(oldPath) + "}";
        await using var current = fixture.Owner(id: "current");
        var invalid = (await fixture.RunOwnedAsync(current)).Result;
        Assert.False(invalid.Passed);
        Assert.Equal("current", invalid.TestResultAttemptId);
        Assert.Empty(invalid.NormalizedTestResultPaths!);
        fixture.Output = CrosswalkAcceptanceFixture.Report((CrosswalkAcceptanceFixture.Id, "passed"));
        var next = (await fixture.RunOwnedAsync(current)).Result;
        Assert.True(next.Passed);
        Assert.Equal(1, next.TestResultRunOrdinal);
        Assert.NotEqual(oldPath, Assert.Single(next.NormalizedTestResultPaths!));
        Assert.True(File.Exists(oldPath));
    }

    [Fact]
    public async Task DiscoveryStartFailure_IsTypedAndDoesNotRunTests()
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        fixture.Handler = (_, _) => throw new InvalidOperationException("cannot start adapter");
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(CrosswalkDiagnosticCategory.StartFailed, Assert.Single(check.CrosswalkDiagnostics!).Category);
        Assert.Single(fixture.Calls);
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Theory]
    [InlineData(false, CrosswalkDiagnosticCategory.Timeout)]
    [InlineData(true, CrosswalkDiagnosticCategory.CaptureLimited)]
    public async Task InterruptedDiscovery_PreservesStderrAndCannotRunTests(bool captureLimited, CrosswalkDiagnosticCategory expected)
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        fixture.CaptureLimited = captureLimited;
        fixture.TimedOut = !captureLimited;
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(expected, check.CrosswalkDiagnostics![0].Category);
        Assert.Equal(fixture.Stderr, check.ProcessStderr);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task ReceiptPublicationFailure_CannotBecomeGreenOrAdoptOldEvidence()
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        await using var owner = fixture.Owner();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(owner.ResultsPrefix)!)!);
        File.WriteAllText(Path.GetDirectoryName(owner.ResultsPrefix)!, "blocked artifact directory");
        var check = (await fixture.RunOwnedAsync(owner)).Result;
        Assert.False(check.Passed);
        Assert.Contains(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.ReceiptFailure);
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task DeclaredCrosswalkEvidence_IsAcceptedByCohortConsumer()
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        var check = await fixture.RunAsync();
        Assert.True(check.Passed);
        Assert.Equal(0, check.TestResultRunOrdinal);
        var classified = AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResultWithPaths(
            new(true, false, 0, null, Checks: [check], TestResultPaths: check.NormalizedTestResultPaths)
            {
                AttemptId = check.TestResultAttemptId, RequiredCrosswalkChecks = [check.Name], RequiredTestCheckNames = [check.Name]
            });
        Assert.Equal(AcceptanceCohortGateOutcome.Passed, classified.Classification.Outcome);
        var evidence = Assert.Single(classified.Checks);
        Assert.Equal(0, evidence.RunOrdinal);
        Assert.Equal(1, evidence.ExecutedTestCount);
        Assert.Equal(check.NormalizedTestResultPaths, classified.NormalizedTestResultPaths);
    }
}
