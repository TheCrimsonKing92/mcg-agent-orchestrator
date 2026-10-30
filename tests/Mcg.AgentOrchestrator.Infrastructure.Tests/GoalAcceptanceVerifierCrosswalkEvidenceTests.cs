using System.Text.Json;
using System.Security.Cryptography;
using System.Xml.Linq;
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
    [InlineData("extra-pass", false, CrosswalkDiagnosticCategory.MalformedResults)]
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
        Assert.Equal(fixture.Binary, check.CrosswalkExecutablePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Binary))), check.CrosswalkExecutableSha256);
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
        Assert.Equal(fixture.Binary, roundtrip.CrosswalkExecutablePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Binary))), roundtrip.CrosswalkExecutableSha256);
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
        var resolver = new CrosswalkExecutableResolver(missing ? Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing") : null);
        using var fixture = new CrosswalkAcceptanceFixture(executable: resolver);
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Equal(expected, Assert.Single(check.CrosswalkDiagnostics!).Category);
        Assert.Empty(fixture.Calls);
        Assert.Null(check.CrosswalkExecutablePath);
        Assert.Null(check.CrosswalkExecutableSha256);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(check));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(nameof(check.CrosswalkExecutablePath)).ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(nameof(check.CrosswalkExecutableSha256)).ValueKind);
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
    public async Task SolutionCoverage_WithCrosswalk_PreservesInvocationProvenance()
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "config", "acceptance-manifest.json"),
            AcceptanceManifestTestDefaults.WithEngine("""
            {
              "version": 1,
              "checks": [
                { "name": "crosswalk selection", "type": "crosswalk-test" },
                { "name": "project tests", "type": "dotnet-test", "project": "tests/Sample.Tests.csproj" },
                { "name": "solution tests", "type": "dotnet-test" }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """));
        var crosswalkCalls = 0;
        var testCalls = new List<string[]>();
        var verifier = new GoalAcceptanceVerifier(fixture.TestOverrides, (args, _, _, _) =>
        {
            var command = args.LastOrDefault() ?? "";
            if (command.StartsWith("& '", StringComparison.Ordinal))
            {
                crosswalkCalls++;
                return Task.FromResult(fixture.Result(command.Contains("' list '", StringComparison.Ordinal)
                    ? fixture.Discovery : fixture.Output));
            }
            if (args is ["dotnet", "test", ..])
            {
                testCalls.Add(args);
                GoalAcceptanceVerifierDotnetBuildSlotTestsTrustedBaselineDiscovery.WriteVstestTrx(
                    args, "Sample.Tests.Passes");
                var resultsDirectory = args[Array.IndexOf(args, "--results-directory") + 1];
                var fileName = args[Array.IndexOf(args, "--logger") + 1]["trx;LogFileName=".Length..];
                var trxPath = Path.Combine(resultsDirectory, fileName);
                var document = XDocument.Load(trxPath);
                XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                foreach (var element in document.Root!.DescendantsAndSelf())
                    element.Name = ns + element.Name.LocalName;
                document.Root.Add(new XElement(ns + "ResultSummary", new XElement(ns + "Counters",
                    new XAttribute("total", 1), new XAttribute("executed", 1),
                    new XAttribute("passed", 1), new XAttribute("failed", 0))));
                document.Save(trxPath);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                    "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1."));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
        }) { CrosswalkExecutable = fixture.Executable };

        var result = await verifier.RunAsync(fixture.Root);

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(2, crosswalkCalls);
        var solutionCall = Assert.Single(testCalls);
        Assert.DoesNotContain(solutionCall, arg => arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, result.Checks!.Count);
        var solution = Assert.Single(result.Checks, check => check.Name == "solution tests");
        Assert.Equal(Path.GetFileName(fixture.Prefix), result.AttemptId);
        Assert.Equal(result.AttemptId, solution.TestResultAttemptId);
        Assert.Equal(0, solution.TestResultRunOrdinal);
        Assert.False(solution.TestResultIsExplicitCrossAttemptReuse);
        Assert.True(File.Exists(Assert.Single(solution.TestResultPaths!)));
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(solution.TestResultPaths));
        Assert.Null(solution.NormalizedTestResultWriteFailure);
        Assert.Single(solution.NormalizedTestResultPaths!);
        var project = Assert.Single(result.Checks, check => check.Name == "project tests");
        Assert.Equal([solution.Name], project.CoveredBy);
        Assert.Equal("covered by: solution tests", project.ResultSummary);

        var classified = AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResultWithPaths(result);
        Assert.Equal(AcceptanceCohortGateOutcome.Passed, classified.Classification.Outcome);
        Assert.Equal(2, classified.Checks.Count);
        Assert.All(classified.Checks, check => Assert.Equal(result.AttemptId, check.AttemptId));
        var trx = Assert.Single(classified.Checks, check => check.Format == AcceptanceCohortCheckEvidence.TrxFormat);
        Assert.Equal(solution.Name, trx.CheckName);
        Assert.Equal(solution.TestResultRunOrdinal, trx.RunOrdinal);
        Assert.Equal(1, trx.ExecutedTestCount);
        Assert.Equal(1, Assert.Single(classified.Checks,
            check => check.Format == AcceptanceCohortCheckEvidence.NormalizedFormat).ExecutedTestCount);

        // Removing or substituting provenance must still fail closed at the consumer.
        foreach (var invalidAttempt in new string?[] { null, "unrelated-attempt" })
        {
            var invalid = result with { Checks = result.Checks.Select(check => check.Name == solution.Name
                ? check with { TestResultAttemptId = invalidAttempt } : check).ToArray() };
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure,
                AcceptanceCohortVerificationClassifier.ClassifyCohortVerification(invalid));
        }

        // A producer pass cannot compensate for an invalid declared TRX contract.
        var retainedTrxPath = Assert.Single(solution.TestResultPaths!);
        var validTrx = File.ReadAllText(retainedTrxPath);
        foreach (var missingNamespace in new[] { false, true })
        {
            var invalidTrx = XDocument.Parse(validTrx);
            if (missingNamespace)
            {
                foreach (var element in invalidTrx.Root!.DescendantsAndSelf())
                {
                    element.Name = element.Name.LocalName;
                    element.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Remove();
                }
            }
            else
            {
                invalidTrx.Descendants().Single(element => element.Name.LocalName == "Counters").Remove();
            }
            invalidTrx.Save(retainedTrxPath);
            Assert.False(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(solution.TestResultPaths));
            Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure,
                AcceptanceCohortVerificationClassifier.ClassifyCohortVerification(result));
        }
    }

    [Fact]
    public async Task DeclaredCrosswalkEvidence_IsAcceptedByCohortConsumer()
    {
        using var fixture = new CrosswalkAcceptanceFixture();
        var check = await fixture.RunAsync();
        Assert.True(check.Passed);
        Assert.Equal(0, check.TestResultRunOrdinal);
        var roundtrip = JsonSerializer.Deserialize<AcceptanceCheckResult>(JsonSerializer.Serialize(check))!;
        Assert.Equal(fixture.Binary, roundtrip.CrosswalkExecutablePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Binary))), roundtrip.CrosswalkExecutableSha256);
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
