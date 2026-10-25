using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each fixture owns its source tree, receipt directory and evidence stores.
public sealed class GoTestJsonGateReceiptTests
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    internal static readonly string[] LiveSelectors =
        ["main.TestStdoutIdempotency", "main.TestStdoutWithJSONOutput", "main.TestStderrSeparation"];

    [Fact]
    public void Attach_PassingStream_WritesCoherentTopLevelReceipt()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        CreateGoSources(fixture.Root);

        var attached = Attach(fixture.Root, StreamA);

        var path = Assert.Single(attached.TestResultPaths!);
        Assert.EndsWith(".go-test.trx", path, StringComparison.Ordinal);
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence([path]));
        Assert.Equal(4, attached.ExecutedTestCount);
        Assert.Empty(attached.FailingTestIdentities!);
        Assert.Null(attached.OutputTail);
        var document = XDocument.Load(path);
        var counters = document.Descendants(Trx + "Counters").Single();
        Assert.Equal(new[] { "4", "4", "4", "0" },
            new[] { "total", "executed", "passed", "failed" }.Select(name => counters.Attribute(name)!.Value));
        var definitions = document.Descendants(Trx + "UnitTest").ToArray();
        var main = Assert.Single(definitions, row => row.Attribute("name")!.Value == LiveSelectors[0]);
        var method = main.Element(Trx + "TestMethod")!;
        Assert.Equal("example.com/schemadrift/cmd/schemadrift", method.Attribute("className")!.Value);
        Assert.Equal("cmd/schemadrift", method.Attribute("codeBase")!.Value);
        Assert.Equal(GoTestJsonGateReceipt.AdapterTypeName, method.Attribute("adapterTypeName")!.Value);
        Assert.Equal("example.com/schemadrift/cmd/schemadrift", main.Attribute("storage")!.Value);
        Assert.Contains(definitions, row => row.Attribute("name")!.Value == "drift.TestMissingColumn");
        Assert.Equal(4, definitions.Select(row => Guid.Parse(row.Attribute("id")!.Value)).Distinct().Count());
        Assert.All(document.Descendants(Trx + "UnitTestResult"), row =>
        {
            var definition = Assert.Single(definitions, item => item.Attribute("id")!.Value == row.Attribute("testId")!.Value);
            Assert.Equal(definition.Attribute("name")!.Value, row.Attribute("testName")!.Value);
        });
    }

    [Fact]
    public void Evaluate_PassingReceipt_SatisfiesAllSupportedIdentities()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        CreateGoSources(fixture.Root);
        var receipt = Assert.Single(Attach(fixture.Root, StreamA).TestResultPaths!);
        var (attemptId, _, _) = fixture.WriteAttempt([], additionalPaths: [receipt]);
        string[][] selectorSets =
        [
            LiveSelectors,
            ["example.com/schemadrift/cmd/schemadrift.TestStdoutIdempotency"],
            ["cmd/schemadrift.TestStdoutIdempotency"],
            ["TestStdoutIdempotency"],
            ["drift.TestMissingColumn"],
            ["main"], ["example.com/schemadrift/cmd/schemadrift"], ["cmd/schemadrift"]
        ];
        foreach (var selectors in selectorSets)
        {
            var result = Evaluate(fixture, selectors);
            Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
            Assert.Equal(attemptId, result.SourceId);
        }
        var missing = Evaluate(fixture,
            [LiveSelectors[0], "main.TestTypoName", "drift.TestLargeCorpus"]);
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, missing.Outcome);
        Assert.Equal(new[] { "main.TestTypoName", "drift.TestLargeCorpus" }, missing.Selectors);
        Assert.Equal(attemptId, missing.SourceId);
        foreach (var selector in new[] { "schemadrift.TestStdoutIdempotency" })
        {
            var result = Evaluate(fixture, [selector]);
            Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
            Assert.Equal(new[] { selector }, result.Selectors);
        }
    }

    [Fact]
    public void Evaluate_FailedReceipt_ReportsOnlyTheFailingSelector()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        CreateGoSources(fixture.Root);
        var attached = Attach(fixture.Root, StreamB, failed: true);
        var path = Assert.Single(attached.TestResultPaths!);
        var counters = XDocument.Load(path).Descendants(Trx + "Counters").Single();
        Assert.Equal("3", counters.Attribute("passed")!.Value);
        Assert.Equal("1", counters.Attribute("failed")!.Value);
        Assert.Equal(new[] { "example.com/schemadrift/cmd/schemadrift.TestStdoutWithJSONOutput" },
            attached.FailingTestIdentities);
        var (attemptId, _, _) = fixture.WriteAttempt([], additionalPaths: [path]);

        var result = Evaluate(fixture, LiveSelectors[..2]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(new[] { LiveSelectors[1] }, result.Selectors);
        Assert.Equal(attemptId, result.SourceId);
    }

    [Theory]
    [InlineData("go", "vet", "go vet", "prefix")]
    [InlineData("make", "test", "make test", "prefix")]
    [InlineData("go", "test", "go focused evidence: internal/drift TestMissingColumn", "prefix")]
    [InlineData("go", "test", "go declared-check evidence: fallback", "prefix")]
    [InlineData("go", "test", "go test", null)]
    [InlineData("go", "test", "go test", "")]
    public void PrepareArguments_IneligibleCheck_LeavesArgumentsAndAttachmentUnchanged(
        string command, string verb, string name, string? prefix)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        string[] checkArguments = name.StartsWith("go focused evidence:", StringComparison.Ordinal)
            ? ["test", "-count=1", "-v", "-run", "^(TestMissingColumn)$", "./internal/drift"]
            : [verb, "./..."];
        var check = new AcceptanceManifestCheck { Name = name, Command = command, Arguments = checkArguments };
        string[] arguments = [command, .. checkArguments];
        var resultsPrefix = prefix is null or "" ? prefix : Path.Combine(fixture.Root, prefix);

        Assert.Same(arguments, GoTestJsonGateReceipt.PrepareArguments(check, arguments, resultsPrefix));
        var built = new AcceptanceCheckResult(name, true, 0, null);
        Assert.Same(built, GoTestJsonGateReceipt.Attach(check, [.. arguments, "-json"],
            new(0, StreamA), fixture.Root, resultsPrefix, _ => throw new InvalidOperationException(), built));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void PrepareArguments_EligibleCheck_InsertsOnceAndKeepsExistingJson()
    {
        var check = GoCheck();
        var prepared = GoTestJsonGateReceipt.PrepareArguments(check, ["go", "test", "-count=1", "./..."], "prefix");
        Assert.Equal(new[] { "go", "test", "-json", "-count=1", "./..." }, prepared);
        string[] existing = ["go", "test", "-count=1", "-json", "./..."];
        Assert.Same(existing, GoTestJsonGateReceipt.PrepareArguments(check, existing, "prefix"));
        Assert.Single(existing, argument => argument == "-json");
    }

    [Fact]
    public void Attach_NoTerminalTestEvents_ReturnsSameInstanceWithoutWriting()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        const string stream = """
            {"Action":"output","Package":"example.com/schemadrift/tools","Output":"no tests\n"}
            {"Action":"skip","Package":"example.com/schemadrift/tools"}
            """;
        var built = new AcceptanceCheckResult("go test", true, 0, "original");
        var attached = GoTestJsonGateReceipt.Attach(GoCheck(), ["go", "test", "-json"], new(0, stream),
            fixture.Root, Path.Combine(fixture.Root, "attempt"), _ => throw new InvalidOperationException(), built);

        Assert.Same(built, attached);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public void Attach_MixedEvents_DeduplicatesWithFailureAndDecodesInOrder()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        CreateGoSources(fixture.Root);
        var stream = "{\"Output\":\"first\\n\"}\r\nplain stderr\r\n[1,2]\n{broken}\n" +
            "{\"Package\":42,\"Test\":\"Invalid\",\"Action\":\"pass\"}\n" +
            "{\"Package\":\"example.com/schemadrift/cmd/schemadrift\",\"Test\":\"TestOne\",\"Action\":\"fail\"}\n" +
            "{\"Package\":\"example.com/schemadrift/cmd/schemadrift\",\"Test\":\"TestOne\",\"Action\":\"pass\",\"Output\":\"last\"}";
        var built = new AcceptanceCheckResult("go test", false, 1, "raw", Advisory: true, ResultSummary: "kept");
        var tailCalls = 0;

        var attached = GoTestJsonGateReceipt.Attach(GoCheck(), ["go", "test", "-json"], new(1, stream),
            fixture.Root, Path.Combine(fixture.Root, "attempt"), decoded => { tailCalls++; return decoded; }, built);

        Assert.Equal(1, tailCalls);
        Assert.Equal("first\nplain stderr\r\n[1,2]\n{broken}\nlast", attached.OutputTail);
        Assert.Equal(1, attached.ExecutedTestCount);
        Assert.Equal(new[] { "example.com/schemadrift/cmd/schemadrift.TestOne" }, attached.FailingTestIdentities);
        Assert.Equal(built with
        {
            TestResultPaths = attached.TestResultPaths, ExecutedTestCount = attached.ExecutedTestCount,
            FailingTestIdentities = attached.FailingTestIdentities, OutputTail = attached.OutputTail
        }, attached);
    }

    [Theory]
    [InlineData("module-boundary")]
    [InlineData("missing-module")]
    [InlineData("missing-source")]
    [InlineData("missing-clause")]
    [InlineData("escape")]
    public void Attach_UnresolvedSource_UsesImportPathWithoutDirectoryAlias(string scenario)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        CreateGoSources(fixture.Root);
        var package = "example.com/schemadrift/cmd/schemadrift";
        switch (scenario)
        {
            case "module-boundary": File.WriteAllText(Path.Combine(fixture.Root, "go.mod"), "module example.com/schema"); break;
            case "missing-module": File.Delete(Path.Combine(fixture.Root, "go.mod")); break;
            case "missing-source": File.Delete(Path.Combine(fixture.Root, "cmd/schemadrift/main_test.go")); break;
            case "missing-clause": File.WriteAllText(Path.Combine(fixture.Root, "cmd/schemadrift/main_test.go"), "// no package"); break;
            case "escape": package = "example.com/schemadrift/../outside"; break;
        }
        var stream = $$"""{"Package":"{{package}}","Test":"TestOne","Action":"pass"}""";

        var path = Assert.Single(Attach(fixture.Root, stream).TestResultPaths!);
        var definition = XDocument.Load(path).Descendants(Trx + "UnitTest").Single();
        Assert.Equal(package + ".TestOne", definition.Attribute("name")!.Value);
        Assert.Equal("", definition.Element(Trx + "TestMethod")!.Attribute("codeBase")!.Value);
        var row = Assert.Single(ProspectiveEvidenceTrxRows.Read(path));
        Assert.Null(row.AliasIdentity);
        Assert.Single(ProspectiveEvidenceTrxRows.Match("TestOne", [row]));
    }

    [Fact]
    public void Attach_CommentedQuotedModule_UsesOrdinalFirstSourceAndRootClause()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "go.mod"), "// module comment\nmodule \"example.com/schemadrift\" // suffix\n");
        File.WriteAllText(Path.Combine(fixture.Root, "z_test.go"), "package wrong");
        File.WriteAllText(Path.Combine(fixture.Root, "a_test.go"), "//go:build windows\n/* license */\n package /* comment */ root_test\n");
        var path = Assert.Single(Attach(fixture.Root,
            """{"Package":"example.com/schemadrift","Test":"TestRoot","Action":"pass"}""").TestResultPaths!);

        var row = Assert.Single(ProspectiveEvidenceTrxRows.Read(path));
        Assert.Equal("root.TestRoot", row.ShortIdentity);
        Assert.Null(row.AliasIdentity);
        Assert.Equal("example.com/schemadrift.TestRoot", row.FullIdentity);
    }

    [Fact]
    public void Evaluate_SameClauseAcrossImports_RemainsAmbiguous()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        CreateGoSources(fixture.Root);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "cmd/other"));
        File.WriteAllText(Path.Combine(fixture.Root, "cmd/other/other_test.go"), "package main");
        var path = Assert.Single(Attach(fixture.Root, StreamA + "\n" +
            """{"Package":"example.com/schemadrift/cmd/other","Test":"TestStdoutIdempotency","Action":"pass"}""").TestResultPaths!);
        fixture.WriteAttempt([], additionalPaths: [path]);

        Assert.NotEqual(GateTestObligationEvaluator.Outcome.Satisfied, Evaluate(fixture, [LiveSelectors[0]]).Outcome);
        Assert.Equal(2, ProspectiveEvidenceTrxRows.AmbiguousClassNames(
            ProspectiveEvidenceTrxRows.Match(LiveSelectors[0], ProspectiveEvidenceTrxRows.Read(path)))!.Count);
    }

    internal static void CreateGoSources(string root)
    {
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/schemadrift\n");
        Directory.CreateDirectory(Path.Combine(root, "cmd/schemadrift"));
        Directory.CreateDirectory(Path.Combine(root, "internal/drift"));
        File.WriteAllText(Path.Combine(root, "cmd/schemadrift/main_test.go"), "package main\n");
        File.WriteAllText(Path.Combine(root, "internal/drift/drift_test.go"), "package drift_test\n");
    }

    internal static GateTestObligationEvaluator.Result Evaluate(
        ProspectiveEvidenceGateFixture fixture, IReadOnlyList<string> selectors)
    {
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors);
        return Assert.Single(GateTestObligationEvaluator.Evaluate(kernel.GetGoal(fixture.Goal.Id),
            fixture.OrchestratorDirectory, fixture.CandidateSha));
    }

    private static AcceptanceManifestCheck GoCheck() => new()
    {
        Name = "go test", Type = "command", Command = "go", Arguments = ["test", "-count=1", "./..."]
    };

    private static AcceptanceCheckResult Attach(string root, string stream, bool failed = false) =>
        GoTestJsonGateReceipt.Attach(GoCheck(), ["go", "test", "-json", "-count=1", "./..."],
            new(failed ? 1 : 0, stream), root, Path.Combine(root, "attempt"), value => value,
            new("go test", !failed, failed ? 1 : 0, failed ? "raw" : null));

    internal const string StreamA = """
        {"Time":"2026-10-24T15:03:55.1Z","Action":"start","Package":"example.com/schemadrift/cmd/schemadrift"}
        {"Time":"2026-10-24T15:03:55.2Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency"}
        {"Time":"2026-10-24T15:03:55.2Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency","Output":"=== RUN   TestStdoutIdempotency\n"}
        {"Time":"2026-10-24T15:03:55.9Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency","Output":"--- PASS: TestStdoutIdempotency (0.70s)\n"}
        {"Time":"2026-10-24T15:03:55.9Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency","Elapsed":0.7}
        {"Time":"2026-10-24T15:03:56.0Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutWithJSONOutput"}
        {"Time":"2026-10-24T15:03:56.3Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutWithJSONOutput","Elapsed":0.3}
        {"Time":"2026-10-24T15:03:56.3Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation"}
        {"Time":"2026-10-24T15:03:56.3Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation/empty_input"}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation/empty_input","Elapsed":0.2}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation","Elapsed":0.2}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Output":"ok  \texample.com/schemadrift/cmd/schemadrift\t1.204s\n"}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Elapsed":1.204}
        go: downloading example.com/dep v1.0.0
        {"Time":"2026-10-24T15:03:57.0Z","Action":"run","Package":"example.com/schemadrift/internal/drift","Test":"TestMissingColumn"}
        {"Time":"2026-10-24T15:03:57.1Z","Action":"pass","Package":"example.com/schemadrift/internal/drift","Test":"TestMissingColumn","Elapsed":0.1}
        {"Time":"2026-10-24T15:03:57.1Z","Action":"run","Package":"example.com/schemadrift/internal/drift","Test":"TestLargeCorpus"}
        {"Time":"2026-10-24T15:03:57.1Z","Action":"skip","Package":"example.com/schemadrift/internal/drift","Test":"TestLargeCorpus","Elapsed":0}
        {"Time":"2026-10-24T15:03:57.2Z","Action":"pass","Package":"example.com/schemadrift/internal/drift","Elapsed":0.2}
        """;

    internal const string StreamB = """
        {"Time":"2026-10-24T15:03:55.1Z","Action":"start","Package":"example.com/schemadrift/cmd/schemadrift"}
        {"Time":"2026-10-24T15:03:55.2Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency"}
        {"Time":"2026-10-24T15:03:55.2Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency","Output":"=== RUN   TestStdoutIdempotency\n"}
        {"Time":"2026-10-24T15:03:55.9Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency","Output":"--- PASS: TestStdoutIdempotency (0.70s)\n"}
        {"Time":"2026-10-24T15:03:55.9Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutIdempotency","Elapsed":0.7}
        {"Time":"2026-10-24T15:03:56.0Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutWithJSONOutput"}
        {"Time":"2026-10-24T15:03:56.3Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutWithJSONOutput","Output":"--- FAIL: TestStdoutWithJSONOutput (0.30s)\n"}
        {"Time":"2026-10-24T15:03:56.3Z","Action":"fail","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStdoutWithJSONOutput","Elapsed":0.3}
        {"Time":"2026-10-24T15:03:56.3Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation"}
        {"Time":"2026-10-24T15:03:56.3Z","Action":"run","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation/empty_input"}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation/empty_input","Elapsed":0.2}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"pass","Package":"example.com/schemadrift/cmd/schemadrift","Test":"TestStderrSeparation","Elapsed":0.2}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"output","Package":"example.com/schemadrift/cmd/schemadrift","Output":"ok  \texample.com/schemadrift/cmd/schemadrift\t1.204s\n"}
        {"Time":"2026-10-24T15:03:56.5Z","Action":"fail","Package":"example.com/schemadrift/cmd/schemadrift","Elapsed":1.204}
        go: downloading example.com/dep v1.0.0
        {"Time":"2026-10-24T15:03:57.0Z","Action":"run","Package":"example.com/schemadrift/internal/drift","Test":"TestMissingColumn"}
        {"Time":"2026-10-24T15:03:57.1Z","Action":"pass","Package":"example.com/schemadrift/internal/drift","Test":"TestMissingColumn","Elapsed":0.1}
        {"Time":"2026-10-24T15:03:57.1Z","Action":"run","Package":"example.com/schemadrift/internal/drift","Test":"TestLargeCorpus"}
        {"Time":"2026-10-24T15:03:57.1Z","Action":"skip","Package":"example.com/schemadrift/internal/drift","Test":"TestLargeCorpus","Elapsed":0}
        {"Time":"2026-10-24T15:03:57.2Z","Action":"pass","Package":"example.com/schemadrift/internal/drift","Elapsed":0.2}
        """;
}
