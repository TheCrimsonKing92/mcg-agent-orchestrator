using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each fixture owns its directory; command execution is stubbed.
public sealed class GoPackageFocusedEvidenceTests
{
    internal const string Manifest = """
        {"checks":[
          {"name":"git diff whitespace","type":"command","command":"git","arguments":["diff","--check"]},
          {"name":"go test","type":"command","command":"go","arguments":["test","-count=1","./..."],"timeoutMinutes":7}
        ]}
        """;
    private const string PassingOutput = "--- PASS: TestExtraColumn (0.00s)\n--- PASS: TestMissingColumn (0.01s)";
    private const string FailingOutput = "--- PASS: TestExtraColumn (0.00s)\n--- FAIL: TestMissingColumn (0.01s)";
    private const string EmptyOutput = "testing: warning: no tests to run\nPASS";
    private const string SkippedOutput = "=== RUN   TestOptIn\n    opt-in integration disabled\n--- SKIP: TestOptIn (0.00s)\nPASS";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseReadsTheFirstGoTestCommandCheck(bool withEngine)
    {
        var manifest = Manifest.Replace("\"checks\":[", """
            "checks":[
            {"name":"go vet","type":"command","command":"go","arguments":["vet","./..."]},
            """);
        if (withEngine)
            manifest = manifest.Insert(1, "\"engine\":{},");
        var settings = AcceptanceGateEngineSettings.Parse(manifest);

        Assert.NotNull(settings.DeclaredGoTestCheck);
        Assert.Equal("go test", settings.DeclaredGoTestCheck.Name);
        Assert.Equal(["test", "-count=1", "./..."], settings.DeclaredGoTestCheck.Arguments);
        Assert.Null(AcceptanceGateEngineSettings.Parse("""
            {"checks":[{"name":"go vet","type":"command","command":"go","arguments":["vet","./..."]}]}
            """).DeclaredGoTestCheck);
        Assert.True(GoPackageFocusedEvidence.Applies(settings));
    }

    [Fact]
    public void PackageRequestsBuildScopedGoTestChecks()
    {
        using var fixture = new Fixture();
        const string request = "internal/drift:TestMissingColumn|TestExtraColumn";
        var check = Assert.Single(fixture.Build(request, out var coverage));

        Assert.Equal("go focused evidence: internal/drift TestExtraColumn|TestMissingColumn", check.Name);
        Assert.Equal("command", check.Type);
        Assert.Equal("go", check.Command);
        Assert.Equal(["test", "-count=1", "-v", "-run", "^(TestExtraColumn|TestMissingColumn)$", "./internal/drift"],
            check.Arguments);
        Assert.Equal(7, check.TimeoutMinutes);
        Assert.Equal("focused", coverage.ExecutionMode);
        Assert.Equal("go-package-focused", coverage.ExecutionReason);
        Assert.Equal(request, Assert.Single(coverage.TargetToChecks).Target);
        Assert.Equal([check.Name], coverage.TargetToChecks[0].CheckNames);

        var package = Assert.Single(fixture.Build("./internal/drift/", out coverage));
        Assert.Equal("go focused evidence: internal/drift", package.Name);
        Assert.Equal(["test", "-count=1", "-v", "./internal/drift"], package.Arguments);
        Assert.Equal("focused", coverage.ExecutionMode);
        var duplicate = fixture.Build(
            " ; internal\\drift:TestMissingColumn|FullyQualifiedName~TestExtraColumn|TestMissingColumn; " + request,
            out coverage);
        Assert.Equal(check.Name, Assert.Single(duplicate).Name);
        Assert.Equal(2, coverage.TargetToChecks.Count);
    }

    [Fact]
    public void UnscopableRequestsFallBackToTheDeclaredCheck()
    {
        using var fixture = new Fixture();
        foreach (var request in new[] { "internal/missing:TestX", "internal/drift:TestMissingColumn/empty_input",
            "../outside:TestX", "Core.Tests:FooTests", "internal/drift:", "internal/drift:TestX|",
            "internal/drift:^TestX$", "internal/drift:FooTests" })
        {
            var fallback = Assert.Single(fixture.Build(request, out var coverage));
            Assert.Equal("go declared-check evidence: go test", fallback.Name);
            Assert.Equal("go", fallback.Command);
            Assert.Equal(7, fallback.TimeoutMinutes);
            Assert.Equal(["test", "-v", "-count=1", "./..."], fallback.Arguments);
            Assert.Equal("project", coverage.ExecutionMode);
            Assert.Equal("go-declared-check-fallback", coverage.ExecutionReason);
            Assert.Equal(request, Assert.Single(coverage.TargetToChecks).Target);
            Assert.Equal([fallback.Name], coverage.TargetToChecks[0].CheckNames);
        }

        var checks = fixture.Build("internal/drift:TestMissingColumn; internal/missing:TestX; ../outside:TestX",
            out var mixed);
        Assert.Equal(2, checks.Count);
        Assert.Equal("mixed", mixed.ExecutionMode);
        Assert.Equal("go-package-focused+go-declared-check-fallback", mixed.ExecutionReason);
        Assert.Equal([checks[0].Name], mixed.TargetToChecks[0].CheckNames);
        Assert.Equal([checks[1].Name], mixed.TargetToChecks[1].CheckNames);
        Assert.Equal([checks[1].Name], mixed.TargetToChecks[2].CheckNames);

        var verbose = AcceptanceGateEngineSettings.Parse(Manifest.Replace("\"-count=1\"", "\"-v\",\"-count=1\""));
        var alreadyVerbose = Assert.Single(fixture.Build("missing:TestX", out _, verbose));
        Assert.Equal(verbose.DeclaredGoTestCheck!.Arguments, alreadyVerbose.Arguments);
    }

    [Fact]
    public void DotnetDeclarationsKeepTheExistingRefusal()
    {
        using var fixture = new Fixture();
        var mixed = AcceptanceGateEngineSettings.Parse(Manifest.Replace("\"checks\":[", """
            "checks":[{"type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"},
            """));
        foreach (var settings in new[] { mixed, AcceptanceGateEngineSettings.Parse("{}") })
        {
            Assert.False(GoPackageFocusedEvidence.Applies(settings));
            Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks("internal/drift:TestMissingColumn",
                settings, fixture.Root, out _, out _, out var rejection));
            Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedProject, rejection.Code);
        }
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
            "./internal/drift/", fixture.Settings, out var project));
        Assert.Equal("internal/drift", project);
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
            "C:/elsewhere", fixture.Settings, out project));
        Assert.Equal("go test", project);
        var mtp = AcceptanceGateEngineSettings.Parse(Manifest.Insert(1, """
            "engine":{"mtpInvocations":[{"project":"tests/Sample.Tests/Sample.Tests.csproj",
            "executablePathTemplate":"bin/Sample{executableExtension}","arguments":["{executable}"]}]},
            """));
        Assert.False(GoPackageFocusedEvidence.Applies(mtp));
        Assert.False(GoPackageFocusedEvidence.TryResolveProject("internal/drift", mtp, out _));
    }

    [Fact]
    public void GoOutputBecomesExecutionEvidence()
    {
        using var fixture = new Fixture();
        var planned = fixture.Build("internal/drift:TestMissingColumn|TestExtraColumn", out _);
        var check = Assert.Single(planned);
        var unrelated = new AcceptanceCheckResult("dotnet-test", true, 0, PassingOutput);
        var results = GoPackageFocusedEvidence.AnnotateResults(planned,
            [new(check.Name, true, 0, PassingOutput), new(check.Name, false, 1, FailingOutput),
             new(check.Name, true, 0, EmptyOutput), new(check.Name, true, 0, null), unrelated]);

        Assert.Equal(2, results[0].ExecutedTestCount);
        Assert.Empty(results[0].FailingTestIdentities!);
        Assert.Equal(2, results[1].ExecutedTestCount);
        Assert.Equal(["internal/drift.TestMissingColumn"], results[1].FailingTestIdentities);
        Assert.Equal(0, results[2].ExecutedTestCount);
        Assert.Equal(0, results[3].ExecutedTestCount);
        Assert.Same(unrelated, results[4]);
        Assert.True(GoPackageFocusedEvidence.IsEvidenceCheck(check));
        Assert.False(GoPackageFocusedEvidence.IsEvidenceCheck(fixture.Settings.DeclaredGoTestCheck!));

        var fallback = fixture.Build("missing:TestX", out _);
        var annotated = Assert.Single(GoPackageFocusedEvidence.AnnotateResults(fallback,
            [new(fallback[0].Name, false, 1, "    --- FAIL: TestMissingColumn/empty_input (0.01s)")]));
        Assert.Equal(1, annotated.ExecutedTestCount);
        Assert.Equal(["TestMissingColumn/empty_input"], annotated.FailingTestIdentities);
    }

    [Fact]
    public async Task ArmDispositionFollowsGoTestOutcomes()
    {
        using var fixture = new Fixture();
        var checks = fixture.Build("internal/drift:TestMissingColumn|TestExtraColumn", out var coverage);
        foreach (var (output, passed, expected) in new[] {
            (PassingOutput, true, FindingEvidenceArmDisposition.Green),
            (FailingOutput, false, FindingEvidenceArmDisposition.Red),
            (EmptyOutput, true, FindingEvidenceArmDisposition.Inconclusive) })
        {
            var calls = 0;
            var arm = await FocusedEvidenceExecution.RunFocusedEvidenceArmAsync(
                _ => throw new InvalidOperationException("No scoped runner expected."),
                (_, planned, _, _, _, _, _, _, _) =>
                {
                    calls++;
                    Assert.Same(checks, planned);
                    return Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>(
                        [new(checks[0].Name, passed, passed ? 0 : 1, output)]);
                },
                _ => "", FindingEvidenceArm.Candidate, "candidate", fixture.Root, null, checks, coverage,
                null, null, null, CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Equal(expected, arm.Disposition);
            Assert.Equal(expected == FindingEvidenceArmDisposition.Green, arm.Passed);
            Assert.Equal(output == EmptyOutput ? 0 : 2, Assert.Single(arm.Checks).ExecutedTestCount);
            if (!passed)
                Assert.Equal(["internal/drift.TestMissingColumn"], arm.Checks[0].FailingTestIdentities);
        }
    }

    [Fact]
    public void Annotation_PassAndSkip_DemonstratesBothSelectors()
    {
        using var fixture = new Fixture();
        var checks = fixture.Build("internal/drift:TestExtraColumn;internal/drift:TestOptIn", out var coverage);
        Assert.Equal(2, checks.Count);
        var results = GoPackageFocusedEvidence.AnnotateResults(checks,
            [new(checks[0].Name, true, 0, "--- PASS: TestExtraColumn (0.00s)"),
             new(checks[1].Name, true, 0, SkippedOutput)]);

        Assert.Equal([1, 1], results.Select(result => result.ExecutedTestCount));
        Assert.Null(results[0].SkippedTestIdentities);
        Assert.Equal(["internal/drift.TestOptIn"], results[1].SkippedTestIdentities);
        Assert.All(results, result => Assert.Empty(result.FailingTestIdentities!));
        var notRun = FocusedEvidenceNotRunPlan.Compare(checks, results, coverage, true);
        Assert.Empty(notRun.Checks);
        Assert.Empty(notRun.Selections);
        Assert.Equal(["internal/drift.TestOptIn"], notRun.Skipped);
        Assert.Contains("internal/drift.TestOptIn", JsonSerializer.Serialize(results), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arm_PassAndSkip_IsGreenWithSkippedSummary()
    {
        using var fixture = new Fixture();
        var checks = fixture.Build("internal/drift:TestExtraColumn;internal/drift:TestOptIn", out var coverage);
        Assert.Equal(2, checks.Count);
        var arm = await fixture.RunArmAsync(checks, coverage,
            [new(checks[0].Name, true, 0, "--- PASS: TestExtraColumn (0.00s)"),
             new(checks[1].Name, true, 0, SkippedOutput)]);

        Assert.Equal(FindingEvidenceArmDisposition.Green, arm.Disposition);
        Assert.True(arm.Passed);
        Assert.Empty(arm.NotRunSelections!);
        Assert.NotEqual(FindingEvidenceOutcomeReason.CandidateInconclusive,
            FocusedEvidenceExecution.ClassifyFocusedEvidenceExperiment(arm, arm));
        Assert.Contains("skipped: internal/drift.TestOptIn", arm.Summary.Split(Environment.NewLine));
    }

    [Fact]
    public void Annotation_NoMarkers_RemainsNotRun()
    {
        using var fixture = new Fixture();
        var checks = fixture.Build("internal/drift:TestOptIn", out var coverage);
        var result = Assert.Single(GoPackageFocusedEvidence.AnnotateResults(checks,
            [new(checks[0].Name, true, 0, EmptyOutput)]));

        Assert.Equal(0, result.ExecutedTestCount);
        Assert.Null(result.SkippedTestIdentities);
        Assert.Empty(result.FailingTestIdentities!);
        var missing = Assert.Single(FocusedEvidenceNotRunPlan.Compare(checks, [result], coverage, true).Checks);
        Assert.Equal("not-run:execution-not-demonstrated", missing.Reason);
        Assert.Equal(new FindingEvidenceSelection("internal/drift", "TestOptIn"), Assert.Single(missing.Selections));
    }

    [Fact]
    public async Task Arm_FailBesideSkip_RemainsRed()
    {
        using var fixture = new Fixture();
        var checks = fixture.Build("internal/drift:TestMissingColumn|TestOptIn", out var coverage);
        var arm = await fixture.RunArmAsync(checks, coverage,
            [new(checks[0].Name, false, 1, "--- FAIL: TestMissingColumn (0.01s)\n" + SkippedOutput)]);

        Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
        Assert.False(arm.Passed);
        var result = Assert.Single(arm.Checks);
        Assert.Equal(2, result.ExecutedTestCount);
        Assert.Equal(["internal/drift.TestMissingColumn"], result.FailingTestIdentities);
        Assert.Equal(["internal/drift.TestOptIn"], result.SkippedTestIdentities);
        Assert.Contains("skipped: internal/drift.TestOptIn", arm.Summary.Split(Environment.NewLine));
        Assert.Equal(FindingEvidenceOutcomeReason.CandidateRed,
            FocusedEvidenceExecution.ClassifyFocusedEvidenceExperiment(arm, arm));
    }

    [Fact]
    public async Task Arm_NoExecution_NamesEachSelectorAndKeepsCheckFallback()
    {
        using var fixture = new Fixture();
        var checks = fixture.Build("internal/drift:TestAlpha|TestBeta;internal/drift", out var coverage);
        Assert.Equal(2, checks.Count);
        var arm = await fixture.RunArmAsync(checks, coverage,
            checks.Select(check => new AcceptanceCheckResult(check.Name, true, 0, EmptyOutput)).ToArray());

        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, arm.Disposition);
        var lines = arm.Summary.Split(Environment.NewLine);
        Assert.Contains("undemonstrated internal/drift:TestAlpha not-run:execution-not-demonstrated", lines);
        Assert.Contains("undemonstrated internal/drift:TestBeta not-run:execution-not-demonstrated", lines);
        Assert.Contains("go focused evidence: internal/drift not-run:execution-not-demonstrated", lines);
        Assert.Equal(new FindingEvidenceSelection("internal/drift", "TestAlpha|TestBeta"),
            Assert.Single(arm.NotRunSelections!));
    }

    [Fact]
    public void Summary_PartialResults_ListsOnlyUndemonstratedSelectors()
    {
        using var fixture = new Fixture();
        var checks = fixture.Build("internal/drift:TestExtraColumn|TestOptIn|TestMissing", out var coverage);
        var results = GoPackageFocusedEvidence.AnnotateResults(checks,
            [new(checks[0].Name, true, 0, "--- PASS: TestExtraColumn (0.00s)\n" + SkippedOutput),
             new(checks[0].Name, true, 0, EmptyOutput)]);
        var notRun = FocusedEvidenceNotRunPlan.Compare(checks, results, coverage, true);
        Assert.Single(notRun.Checks);
        var diagnosticLines = notRun.AppendSummary("").Split(Environment.NewLine)
            .Where(line => line.StartsWith("undemonstrated ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(["undemonstrated internal/drift:TestMissing not-run:execution-not-demonstrated"], diagnosticLines);
        Assert.Equal(["internal/drift.TestOptIn"], notRun.Skipped);
    }

    [Theory]
    [InlineData("internal/drift:TestOptIn", "internal/drift.TestOptIn/subcase")]
    [InlineData("missing:TestOptIn", "TestOptIn/subcase")]
    public void Annotation_SubtestSkips_PreservesIdentityAndDuplicateConventions(string request, string identity)
    {
        using var fixture = new Fixture();
        var checks = fixture.Build(request, out _);
        var result = Assert.Single(GoPackageFocusedEvidence.AnnotateResults(checks,
            [new(checks[0].Name, true, 0, "    --- SKIP: TestOptIn/subcase (0.00s)\n    --- SKIP: TestOptIn/subcase (0.00s)")]));

        Assert.Equal(2, result.ExecutedTestCount);
        Assert.Equal([identity, identity], result.SkippedTestIdentities);
        Assert.Empty(result.FailingTestIdentities!);
    }

    [Fact]
    public void CheckReceipt_AbsentSkips_RoundTripsWithoutAddingAField()
    {
        var original = new AcceptanceCheckResult("check", true, 0, null);
        var json = JsonSerializer.Serialize(original);
        Assert.DoesNotContain("SkippedTestIdentities", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<AcceptanceCheckResult>(json)!.SkippedTestIdentities);
        var skipped = original with { SkippedTestIdentities = ["internal/drift.TestOptIn"] };
        Assert.Equal(skipped.SkippedTestIdentities,
            JsonSerializer.Deserialize<AcceptanceCheckResult>(JsonSerializer.Serialize(skipped))!.SkippedTestIdentities);
    }

    [Theory]
    [InlineData("go")]
    [InlineData("/usr/local/bin/GO")]
    [InlineData("C:\\Go\\bin\\go.exe")]
    public void ParseSkipsMalformedEntriesAndPreservesTheFirstCommand(string command)
    {
        var json = JsonSerializer.Serialize(new { name = "first", type = "COMMAND", command,
            arguments = new[] { "test", "./..." } });
        var settings = AcceptanceGateEngineSettings.Parse("""
            {"checks":[null,42,{"arguments":42},{"type":"command","command":"go","arguments":null},
            """ + json + "," + json.Replace("first", "second") + "]}");
        Assert.Equal("first", settings.DeclaredGoTestCheck!.Name);
        Assert.Equal(command, settings.DeclaredGoTestCheck.Command);
    }

    [Fact]
    public void BlankRequestsAreRejectedAndNestedTestFilesDoNotScope()
    {
        using var fixture = new Fixture();
        Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(" ; ; ", fixture.Settings, fixture.Root,
            out var checks, out _, out var rejection));
        Assert.Empty(checks);
        Assert.Equal(FocusedEvidenceRejectionCode.EmptyRequest, rejection.Code);
        var fallback = Assert.Single(fixture.Build("internal:TestX", out var coverage));
        Assert.Equal("go declared-check evidence: go test", fallback.Name);
        Assert.Equal("project", coverage.ExecutionMode);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal AcceptanceGateEngineSettings Settings { get; } = AcceptanceGateEngineSettings.Parse(Manifest);

        internal Fixture()
        {
            var package = Path.Combine(Root, "internal", "drift");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "drift.go"), "package drift");
            File.WriteAllText(Path.Combine(package, "drift_test.go"), "package drift");
        }

        internal IReadOnlyList<AcceptanceManifestCheck> Build(string request, out FocusedEvidenceCoverage coverage,
            AcceptanceGateEngineSettings? settings = null)
        {
            Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, settings ?? Settings, Root,
                out var checks, out coverage, out var rejection), rejection.Detail);
            return checks;
        }

        internal async Task<FocusedEvidenceArmRunResult> RunArmAsync(
            IReadOnlyList<AcceptanceManifestCheck> checks, FocusedEvidenceCoverage coverage,
            IReadOnlyList<AcceptanceCheckResult> results)
        {
            var calls = 0;
            var arm = await FocusedEvidenceExecution.RunFocusedEvidenceArmAsync(
                _ => throw new InvalidOperationException("No scoped runner expected."),
                (_, planned, _, _, _, _, _, _, _) =>
                {
                    calls++;
                    Assert.Same(checks, planned);
                    return Task.FromResult(results);
                },
                _ => "", FindingEvidenceArm.Candidate, "candidate", Root, null, checks, coverage,
                null, null, null, CancellationToken.None);
            Assert.Equal(1, calls);
            return arm;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
