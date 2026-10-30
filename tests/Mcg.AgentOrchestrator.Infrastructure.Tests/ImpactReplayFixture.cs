using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its repository, receipts, reports and detached checkouts.
internal sealed class ImpactReplayFixture : IDisposable
{
    private const string TestProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = ConductorDriverTests.CreateTempDirectory();
    private readonly string _main;
    private readonly string _branch;
    private readonly bool _changedFooTests;
    private readonly bool _extraBetaClass;
    private string StateDirectory { get; }
    public string FlakeLedgerPath => Path.Combine(_root, ".orchestrator", "lane-flakes.jsonl");
    public string OutputDirectory => Path.Combine(StateDirectory, "reports");
    public string CheckoutParent => Path.Combine(_root, "checkouts");

    public ImpactReplayFixture(int changedSourceCount = 1, bool changedFooTests = false,
        bool extraBetaClass = false, IReadOnlyDictionary<string, string>? changedTestFiles = null,
        bool separateStateDirectory = false)
    {
        StateDirectory = separateStateDirectory
            ? Path.Combine(_root, ".orchestrator", "projects", "fixture")
            : Path.Combine(_root, ".orchestrator");
        _changedFooTests = changedFooTests;
        _extraBetaClass = extraBetaClass;
        Git("init", "--quiet", "--template=");
        Git("config", "user.name", "Impact replay fixture");
        Git("config", "user.email", "impact-replay@example.invalid");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Git("config", "core.hooksPath", Path.Combine(_root, "no-hooks"));
        Write(".gitignore", ".orchestrator/\ncheckouts/\n");
        Write("src/Sample/Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/Sample/Alpha.cs", "namespace Sample; public class Alpha { public int Value => 1; }");
        Write("src/Sample/Beta.cs", "namespace Sample; public class Beta { public int Value => 1; }");
        Write(TestProject + "/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="../../src/Sample/Sample.csproj" /></ItemGroup>
            </Project>
            """);
        Write(TestProject + "/AlphaTests.cs", """
            namespace SampleTests;
            public class AlphaTests { [Xunit.Fact] public void Works() { var subject = new Sample.Alpha(); } }
            """);
        Write(TestProject + "/BetaTests.cs", """
            namespace SampleTests;
            public class BetaTests { [Xunit.Fact] public void Works() { var subject = new Sample.Beta(); } }
            """);
        Write(TestProject + "/FloorTests.cs", """
            namespace SampleTests;
            public class FloorTests { [Xunit.Fact] public void Works() { } }
            """);
        if (extraBetaClass)
            Write(TestProject + "/GammaTests.cs", """
                namespace SampleTests;
                public class GammaTests { [Xunit.Fact] public void Works() { } }
                """);
        Write("config/acceptance-manifest.json", """
            {
              "version": 1,
              "engine": {
                "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
                "infrastructureTestLanes": [
                  { "name": "Alpha lane", "filter": "FullyQualifiedName~AlphaTests", "estimatedSerialSeconds": 11 },
                  { "name": "Beta lane", "filter": "FullyQualifiedName~BetaTests", "estimatedSerialSeconds": 23.5 },
                  { "name": "Chaos gate", "filter": "FullyQualifiedName~FloorTests", "estimatedSerialSeconds": 7 }
                ]
              },
              "checks": [{ "name": "Infrastructure tests", "type": "dotnet-test",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj" }]
            }
            """);
        var manifestPath = Path.Combine(_root, "config/acceptance-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var lanes = manifest["engine"]!["infrastructureTestLanes"]!.AsArray();
        if (extraBetaClass) lanes[1]!["filter"] = "FullyQualifiedName~BetaTests|FullyQualifiedName~GammaTests";
        if (changedFooTests)
            lanes.Add(JsonNode.Parse("""
                { "name": "Foo lane", "filter": "FullyQualifiedName~FooTests", "estimatedSerialSeconds": 5 }
                """));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Commit();
        _main = Git("rev-parse", "HEAD").Trim();
        Write("src/Sample/Alpha.cs", "namespace Sample; public class Alpha { public int Value => 2; }");
        for (var i = 1; i < changedSourceCount; i++)
            Write($"src/Sample/Extra{i}.cs", $"namespace Sample; public class Extra{i} {{ }}");
        if (changedFooTests)
            Write(TestProject + "/FooTests.cs", """
                namespace SampleTests;
                public class FooTests { [Xunit.Fact] public void Works() { } }
                """);
        foreach (var (path, text) in changedTestFiles ?? new Dictionary<string, string>())
            Write(TestProject + "/" + path, text);
        Commit();
        _branch = Git("rev-parse", "HEAD").Trim();
        Assert.NotEqual(_main, _branch);
        // Leave the execution checkout at main, so successful selection must read the historical worktree.
        Git("checkout", "--quiet", "--detach", _main);
    }

    public void AddAttempt(string id, bool failingBeta = false, bool rerunBetaPass = false,
        bool earlierBetaPass = false, bool rerunBetaFailure = false, bool rerunOtherLanePass = false)
    {
        var directory = Path.Combine(StateDirectory, "acceptance-gate-attempts", "fixture-goal") + Path.DirectorySeparatorChar;
        var trxPaths = new List<string>();
        var cases = new List<(string Slug, string ClassName, bool Failed)>
            { ("alpha-lane", "AlphaTests", false), ("beta-lane", "BetaTests", failingBeta), ("chaos-gate", "FloorTests", false) };
        if (_changedFooTests) cases.Add(("foo-lane", "FooTests", false));
        foreach (var (laneSlug, className, failed) in cases)
        {
            var name = $"{id}.infrastructure-tests-{laneSlug}{(earlierBetaPass && laneSlug == "beta-lane" ? "-run-1" : "")}.trx";
            trxPaths.Add(name);
            Write(directory + name, $$"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestDefinitions><UnitTest id="test-1" name="Works">
                    <TestMethod className="SampleTests.{{className}}" name="Works" />
                  </UnitTest></TestDefinitions>
                  <Results><UnitTestResult testId="test-1" testName="SampleTests.{{className}}.Works"
                    outcome="{{(failed ? "Failed" : "Passed")}}" /></Results>
                </TestRun>
                """);
            if (_extraBetaClass && laneSlug == "beta-lane")
                AppendTrxResult(directory + name, "GammaTests", failed ? "Failed" : "Passed");
        }
        if (rerunBetaPass || earlierBetaPass || rerunBetaFailure || rerunOtherLanePass)
        {
            var lane = rerunOtherLanePass ? "alpha-lane" : "beta-lane";
            var name = $"{id}.infrastructure-tests-{lane}{(earlierBetaPass ? "" : "-run-1")}.trx";
            trxPaths.Add(name);
            Write(directory + name, "<TestRun><TestDefinitions /><Results /></TestRun>");
            AppendTrxResult(directory + name, "BetaTests", "Passed");
            if (rerunBetaFailure) AppendTrxResult(directory + name, "BetaTests", "Failed", "StillFails");
            SetTrxCompletion(directory + name, "Completed");
        }
        Write(directory + id + ".attempt.json", JsonSerializer.Serialize(new
        {
            attemptId = id, goalId = "fixture-goal", kind = "gate",
            startedAt = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero),
            branchHeadSha = _branch, mainHeadSha = _main, testResultPaths = trxPaths
        }, Json));
        Write(directory + id + ".result.json", JsonSerializer.Serialize(new
        {
            kind = "completed", branchHeadSha = _branch, mainHeadSha = _main
        }, Json));
    }

    public void AddFlake(string attemptId, string lane = "Infrastructure tests: Beta lane",
        string className = "BetaTests", string outcome = AcceptanceLaneRerunEvidence.Flake)
    {
        var row = new AcceptanceLaneFlakeRow("fixture-goal", attemptId, lane, _branch,
            "first", [], [$"SampleTests.{className}.Works"], "first failed", "rerun", [], [], null,
            outcome, new DateTimeOffset(2026, 10, 28, 13, 0, 0, TimeSpan.Zero));
        new AcceptanceLaneFlakeLedger(_root).Append(row);
    }

    public void RewriteBetaRerun(string attemptId, (string Method, string Outcome)[] results,
        string? summaryOutcome = "Completed", int? discovered = null)
    {
        var path = Path.Combine(StateDirectory, "acceptance-gate-attempts", "fixture-goal",
            $"{attemptId}.infrastructure-tests-beta-lane-run-1.trx");
        Write(path, "<TestRun><TestDefinitions /><Results /></TestRun>");
        foreach (var (method, outcome) in results) AppendTrxResult(path, "BetaTests", outcome, method);
        if (summaryOutcome is not null) SetTrxCompletion(path, summaryOutcome, discovered);
    }

    public void AddBetaFailure(string attemptId, string method) => AppendTrxResult(
        Path.Combine(StateDirectory, "acceptance-gate-attempts", "fixture-goal",
            $"{attemptId}.infrastructure-tests-beta-lane.trx"), "BetaTests", "Failed", method);

    private static void SetTrxCompletion(string path, string outcome, int? discovered = null)
    {
        var document = XDocument.Load(path);
        var ns = document.Root!.Name.Namespace;
        var results = document.Descendants(ns + "UnitTestResult").ToArray();
        var notExecuted = results.Count(result => result.Attribute("outcome")?.Value == "NotExecuted");
        document.Root.Add(new XElement(ns + "ResultSummary", new XAttribute("outcome", outcome),
            new XElement(ns + "Counters", new XAttribute("total", discovered ?? results.Length),
                new XAttribute("executed", results.Length - notExecuted),
                new XAttribute("notExecuted", notExecuted))));
        document.Save(path);
    }

    private void AppendTrxResult(string relativePath, string className, string outcome, string method = "Works")
    {
        var path = Path.Combine(_root, relativePath);
        var document = XDocument.Load(path);
        var ns = document.Root!.Name.Namespace;
        var id = Guid.NewGuid().ToString("N");
        document.Root.Element(ns + "TestDefinitions")!.Add(new XElement(ns + "UnitTest",
            new XAttribute("id", id), new XAttribute("name", method),
            new XElement(ns + "TestMethod", new XAttribute("className", "SampleTests." + className), new XAttribute("name", method))));
        document.Root.Element(ns + "Results")!.Add(new XElement(ns + "UnitTestResult",
            new XAttribute("testId", id), new XAttribute("testName", $"SampleTests.{className}.{method}"), new XAttribute("outcome", outcome)));
        document.Save(path);
    }

    public (ImpactReplayReport Report, string Markdown) Run(bool allowSkippedInputs = false)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliImpactReplayCommand.Run(
            ["impact-replay", "--since", "2026-10-01", "--limit", "100", "--out", OutputDirectory],
            _root, StateDirectory, CheckoutParent, output, error);
        Assert.True(exit == 0, $"exit={exit}; stdout={output}; stderr={error}");
        Assert.Equal("", error.ToString());
        var report = JsonSerializer.Deserialize<ImpactReplayReport>(
            File.ReadAllText(Path.Combine(OutputDirectory, "impact-replay.json")), Json);
        Assert.NotNull(report);
        if (!allowSkippedInputs) Assert.Empty(report.SkippedInputs);
        Assert.Empty(report.UnreplayableAttempts);
        Assert.Empty(report.CleanupErrors);
        return (report, File.ReadAllText(Path.Combine(OutputDirectory, "impact-replay.md")));
    }

    public string[] StateSnapshot() => Directory.EnumerateFileSystemEntries(StateDirectory, "*", SearchOption.AllDirectories)
        .Where(path => path != OutputDirectory && !path.StartsWith(OutputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .Select(path => Path.GetRelativePath(StateDirectory, path) + ":" +
            (File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "directory"))
        .Order(StringComparer.Ordinal).ToArray();

    public void Write(string relativePath, string text)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void Commit()
    {
        Git("add", ".");
        Git("commit", "--quiet", "-m", "fixture");
    }

    public string Git(params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(_root, args, new Dictionary<string, string>
        {
            ["HOME"] = _root,
            ["USERPROFILE"] = _root
        });
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
