using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Xunit;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class AcceptancePartitionVerdictCacheTestReuseShadowTests : IDisposable
{
    private static readonly GoalId Goal = new("ed166ed190cd4c23ab620e95fffeddb7");
    private const string SelectedPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SelectedShadowFixtureTests.cs";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-shadow-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CompleteAttempt_LeavesShadowRecordToCompleteShadows()
    {
        var root = CreateRoot("candidate");
        var check = Partition("ShadowA");
        var calls = 0;
        var cache = CreateCache(root, [check], (_, _, _) => { calls++; return [SelectedPath]; });
        cache.RecordExecution(check, Result(check, Trx(new TrxCase("Ns.SelectedShadowFixtureTests"))));
        Assert.NotNull(cache.CompleteAttempt());
        Assert.Equal(0, calls);
        Assert.False(File.Exists(RecordPath(root)));
        Assert.True(cache.HasPendingShadows);
        cache.CompleteShadows();
        Assert.Equal(1, calls);
        Assert.True(File.Exists(RecordPath(root)));
        Assert.False(cache.HasPendingShadows);
        cache.CompleteShadows();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Observe_NeverResolvesPlanUntilCompleteShadows()
    {
        var root = CreateRoot("candidate");
        var first = Partition("ShadowA");
        var second = Partition("ShadowB");
        var calls = 0;
        var cache = CreateCache(root, [first, second], (_, _, _) => { calls++; return [SelectedPath]; });
        cache.RecordExecution(first, Result(first, Trx(new TrxCase("Ns.SelectedShadowFixtureTests"))) with
        {
            TestResultPaths = [Trx(new TrxCase("Ns.SelectedShadowFixtureTests")),
                Trx(new TrxCase("Ns.UnselectedShadowFixtureTests", "Failed"))]
        });
        cache.RecordExecution(second, Result(second, Trx(new TrxCase("Ns.OtherShadowFixtureTests"))));
        Assert.Equal(0, calls);
        cache.CompleteAttempt();
        Assert.Equal(0, calls);
        cache.CompleteShadows();
        Assert.Equal(1, calls);
        using var record = ReadRecord(root);
        Assert.Equal(3, record.RootElement.GetProperty("classes").GetArrayLength());
    }

    [Fact]
    public void ThrowingResolver_ObserveKeepsRowsAndCompleteShadowsRecordsUnavailable()
    {
        var root = CreateRoot("candidate");
        var check = Partition("ShadowA");
        var calls = 0;
        var cache = CreateCache(root, [check], (_, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("changed files unavailable");
        });
        cache.RecordExecution(check, Result(check, Trx(new TrxCase("Ns.SelectedShadowFixtureTests"),
            new TrxCase("Ns.UnselectedShadowFixtureTests", "Failed")), false));
        Assert.Equal(0, calls);
        cache.CompleteAttempt();
        Assert.Null(Record.Exception(cache.CompleteShadows));
        Assert.Equal(1, calls);
        using var record = ReadRecord(root);
        Assert.Equal("unavailable:changed-files:InvalidOperationException", record.RootElement.GetProperty("plan_status").GetString());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("changed_file_count").ValueKind);
        Assert.Equal(2, record.RootElement.GetProperty("classes").GetArrayLength());
        AssertRow(record.RootElement, "SelectedShadowFixtureTests", check.Name, "run", "selection-degraded:unavailable", "passed");
        AssertRow(record.RootElement, "UnselectedShadowFixtureTests", check.Name, "run", "selection-degraded:unavailable", "failed");
    }

    [Fact]
    public void CompleteAttempt_RecordsSelectedAndUnselectedExecutedClasses()
    {
        var root = CreateRoot("candidate");
        var first = Partition("ShadowA");
        var second = Partition("ShadowB");
        var resolverCalls = 0;
        var cache = CreateCache(root, [first, second], (_, main, commit) =>
        {
            Assert.Equal("main-a", main);
            Assert.Equal("commit-a", commit);
            resolverCalls++;
            return [SelectedPath];
        });
        cache.RecordExecution(first, Result(first, Trx(new TrxCase("Ns.SelectedShadowFixtureTests"))));
        cache.RecordExecution(second, Result(second, Trx(new TrxCase("Ns.UnselectedShadowFixtureTests"))));
        Assert.NotNull(cache.CompleteAttempt());
        cache.CompleteShadows();

        using var record = ReadRecord(root);
        var json = record.RootElement;
        Assert.Equal("main-a", json.GetProperty("main_sha").GetString());
        Assert.Equal("tree-a", json.GetProperty("candidate_tree_sha").GetString());
        Assert.Equal(1, json.GetProperty("changed_file_count").GetInt32());
        Assert.Equal("resolved", json.GetProperty("plan_status").GetString());
        AssertRow(json, "SelectedShadowFixtureTests", first.Name, "run", "selected", "passed");
        AssertRow(json, "UnselectedShadowFixtureTests", second.Name, "would-skip", "unselected", "passed");
        Assert.Equal(1, Summary(json, "would_skip_count"));
        Assert.Equal(0, Summary(json, "would_skip_failed_count"));
        Assert.Equal(1, resolverCalls);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(RecordPath(root))!, "*.tmp"));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("Aborted")]
    [InlineData("NotRunnable")]
    public void FatalOutcome_ReportsFailedWouldSkipAndProtectsExclusiveLane(string outcome)
    {
        var root = CreateRoot("candidate");
        var first = Partition("ShadowA");
        var exclusive = Partition("ShadowB", ["shared-resource"]);
        var cache = CreateCache(root, [first, exclusive], SelectedFiles);
        cache.RecordExecution(first, Result(first, Trx(new TrxCase("Ns.UnselectedShadowFixtureTests", outcome)), false));
        cache.RecordExecution(exclusive, Result(exclusive, Trx(new TrxCase("Ns.ExclusiveShadowFixtureTests"))));
        Assert.NotNull(cache.CompleteAttempt());
        cache.CompleteShadows();

        using var record = ReadRecord(root);
        AssertRow(record.RootElement, "UnselectedShadowFixtureTests", first.Name, "would-skip", "unselected", "failed");
        AssertRow(record.RootElement, "ExclusiveShadowFixtureTests", exclusive.Name, "run", "exclusive-resource-lane", "passed");
        Assert.Equal(1, Summary(record.RootElement, "would_skip_failed_count"));
        Assert.Equal(["UnselectedShadowFixtureTests"], FailedClasses(record.RootElement));
    }

    [Fact]
    public void ClassReduction_HandlesNestedGenericsAndDottedTheoryArguments()
    {
        var root = CreateRoot("candidate");
        var check = Partition("ShadowA");
        var cache = CreateCache(root, [check], (_, _, _) =>
        [
            SelectedPath,
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OuterShadowFixtureTests.cs"
        ]);
        cache.RecordExecution(check, Result(check, Trx(
            new("Ns.OuterShadowFixtureTests`1+Inner`1"),
            new("Ns.SelectedShadowFixtureTests", Display: "Case(value: a.b.c)"),
            new(null, Display: "Ns.InventedClass.Method(value: a.b.c)"),
            new("Ns.SkippedShadowFixtureTests", "NotExecuted"),
            new("Ns.UnknownShadowFixtureTests", "Unknown"))));
        cache.CompleteAttempt();
        cache.CompleteShadows();

        using var record = ReadRecord(root);
        AssertRow(record.RootElement, "OuterShadowFixtureTests", check.Name, "run", "selected", "passed");
        AssertRow(record.RootElement, "SelectedShadowFixtureTests", check.Name, "run", "selected", "passed");
        Assert.Equal(2, record.RootElement.GetProperty("classes").GetArrayLength());
        Assert.Equal(0, Summary(record.RootElement, "would_skip_count"));
    }

    [Fact]
    public void Rows_GroupByClassAndCheckAndRetainAnyFatalOutcome()
    {
        var root = CreateRoot("candidate");
        var first = Partition("ShadowA");
        var second = Partition("ShadowB");
        var cache = CreateCache(root, [first, second], SelectedFiles);
        var failed = Trx(new TrxCase("Ns.UnselectedShadowFixtureTests", "Failed"));
        cache.RecordExecution(first, Result(first, failed, false));
        cache.RecordExecution(first, Result(first, Trx(new TrxCase("Ns.UnselectedShadowFixtureTests"))));
        cache.RecordExecution(second, Result(second, failed, false));
        cache.CompleteAttempt();
        cache.CompleteShadows();

        using var record = ReadRecord(root);
        Assert.Equal(2, record.RootElement.GetProperty("classes").GetArrayLength());
        Assert.Equal(2, Summary(record.RootElement, "would_skip_count"));
        Assert.Equal(2, Summary(record.RootElement, "would_skip_failed_count"));
        Assert.Equal(["UnselectedShadowFixtureTests"], FailedClasses(record.RootElement));
    }

    [Theory]
    [InlineData("broad", "broad-verification")]
    [InlineData("degraded", "selection-degraded:Unreadable")]
    [InlineData("throws", "unavailable:changed-files:InvalidOperationException")]
    [InlineData("empty", "unavailable:no-changed-files")]
    public void UnsafePlan_ClaimsNoSkipsAndPreservesReceiptAndReuse(string cause, string status)
    {
        var root = CreateRoot("candidate");
        var baseline = CreateRoot("baseline");
        if (cause == "degraded")
        {
            foreach (var path in new[] { root, baseline })
            {
                File.WriteAllText(Path.Combine(path, "Mcg.AgentOrchestrator.sln"), string.Empty);
                var source = Path.Combine(path, "src", "Mcg.AgentOrchestrator.Core", "Shadow.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.WriteAllText(source, "public class Shadow { }");
            }
        }
        var check = Partition("ShadowA");
        var result = Result(check, Trx(new TrxCase("Ns.UnselectedShadowFixtureTests")));
        IReadOnlyList<string> Resolve(string _, string __, string ___) => cause switch
        {
            "broad" => ["Directory.Build.props"],
            "degraded" => ["src/Mcg.AgentOrchestrator.Core/Shadow.cs"],
            "empty" => [],
            _ => throw new InvalidOperationException("changed files unavailable")
        };
        var cache = CreateCache(root, [check], Resolve);
        var control = CreateCache(baseline, [check]);
        Assert.Null(cache.TryReuse(check));
        Assert.Null(control.TryReuse(check));
        cache.RecordExecution(check, result);
        control.RecordExecution(check, result);
        AssertSameReceipt(cache.CompleteAttempt(), control.CompleteAttempt());
        cache.CompleteShadows();
        control.CompleteShadows();
        AssertSameReuse(root, baseline, check, Resolve);

        using var record = ReadRecord(root);
        Assert.Equal(status, record.RootElement.GetProperty("plan_status").GetString());
        Assert.Equal(0, Summary(record.RootElement, "would_skip_count"));
        Assert.Equal(0, Summary(record.RootElement, "would_skip_failed_count"));
        Assert.Equal("run", Assert.Single(record.RootElement.GetProperty("classes").EnumerateArray()).GetProperty("decision").GetString());
        if (cause == "throws")
            Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("changed_file_count").ValueKind);
    }

    [Fact]
    public void UnfilteredInfrastructurePlan_SelectsEveryExecutedClass()
    {
        var root = CreateRoot("candidate");
        var check = Partition("ShadowA");
        var cache = CreateCache(root, [check], (_, _, _) => ["src/Mcg.AgentOrchestrator.Infrastructure/Shadow.cs"]);
        cache.RecordExecution(check, Result(check, Trx(new TrxCase("Ns.UnselectedShadowFixtureTests"))));
        cache.CompleteAttempt();
        cache.CompleteShadows();
        using var record = ReadRecord(root);
        Assert.Equal("resolved", record.RootElement.GetProperty("plan_status").GetString());
        AssertRow(record.RootElement, "UnselectedShadowFixtureTests", check.Name, "run", "selected", "passed");
        Assert.Equal(0, Summary(record.RootElement, "would_skip_count"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShadowWriteFailure_PreservesReceiptJournalAndReuse(bool passed)
    {
        var root = CreateRoot("candidate");
        var baseline = CreateRoot("baseline");
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator"));
        var blockedPath = Path.Combine(root, ".orchestrator", "test-reuse-shadow");
        File.WriteAllText(blockedPath, "block directory creation");
        var check = Partition("ShadowA");
        var result = Result(check, Trx(new TrxCase("Ns.UnselectedShadowFixtureTests", passed ? "Passed" : "Failed")), passed);
        var cache = CreateCache(root, [check], SelectedFiles);
        var control = CreateCache(baseline, [check]);
        cache.RecordExecution(check, result);
        control.RecordExecution(check, result);
        AssertSameReceipt(cache.CompleteAttempt(), control.CompleteAttempt());
        cache.CompleteShadows();
        control.CompleteShadows();
        AssertSameReuse(root, baseline, check, SelectedFiles, passed);
        Assert.False(File.Exists(RecordPath(root)));
        Assert.Equal("block directory creation", File.ReadAllText(blockedPath));
        Assert.Contains("acceptance:partition-verdict-cache", File.ReadAllText(cache.JournalPath));
        Assert.Contains("acceptance:partition-verdict", File.ReadAllText(cache.JournalPath));
        using var baselineRecord = ReadRecord(baseline);
        Assert.StartsWith("unavailable:changed-files:", baselineRecord.RootElement.GetProperty("plan_status").GetString());
    }

    [Fact]
    public void MissingTrx_DoesNotDiscardOtherObservedPartitions()
    {
        var root = CreateRoot("candidate");
        var check = Partition("ShadowA");
        var cache = CreateCache(root, [check], SelectedFiles);
        var valid = Trx(new TrxCase("Ns.UnselectedShadowFixtureTests"));
        cache.RecordExecution(check, Result(check, Path.Combine(_root, "missing.trx")) with
        {
            TestResultPaths = [Path.Combine(_root, "missing.trx"), valid]
        });
        cache.CompleteAttempt();
        cache.CompleteShadows();
        using var record = ReadRecord(root);
        AssertRow(record.RootElement, "UnselectedShadowFixtureTests", check.Name, "would-skip", "unselected", "passed");
    }

    [Fact]
    public void GoalWorktree_RecordLivesUnderHostStateRoot()
    {
        var host = CreateRoot("host");
        var worktree = Path.Combine(host, ".orchestrator-worktrees", "candidate");
        Directory.CreateDirectory(worktree);
        var check = Partition("ShadowA");
        var cache = CreateCache(worktree, [check], SelectedFiles);
        cache.RecordExecution(check, Result(check, Trx(new TrxCase("Ns.SelectedShadowFixtureTests"))));
        cache.CompleteAttempt();
        cache.CompleteShadows();
        using var record = ReadRecord(host);
        AssertRow(record.RootElement, "SelectedShadowFixtureTests", check.Name, "run", "selected", "passed");
        Assert.False(File.Exists(RecordPath(worktree)));
    }

    private string CreateRoot(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static AcceptancePartitionVerdictCache CreateCache(string root,
        IReadOnlyList<AcceptanceManifestCheck> checks,
        Func<string, string, string, IReadOnlyList<string>>? resolver = null, string attempt = "attempt-one") =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(Goal, root, checks, 5, false,
                _ => "tree-a", _ => "main-a", _ => "commit-a", () => attempt,
                () => "manifest-a", () => false, ResolveClosureHash: _ => "closure-a",
                ResolveChangedFiles: resolver)));

    private static IReadOnlyList<string> SelectedFiles(string _, string __, string ___) => [SelectedPath];

    private static AcceptanceManifestCheck Partition(string id, IReadOnlyList<string>? resources = null) => new()
    {
        Name = $"infrastructure tests: {id}",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", $"FullyQualifiedName~{id}"],
        ExclusiveResourceKeys = resources ?? []
    };

    private static AcceptanceCheckResult Result(AcceptanceManifestCheck check, string trx, bool passed = true) =>
        new(check.Name, passed, passed ? 0 : 1, passed ? null : "failed",
            TestResultPaths: [trx], DurationMilliseconds: 100);

    private string Trx(params TrxCase[] cases)
    {
        var directory = CreateRoot("fixtures");
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var definitions = new XElement(ns + "TestDefinitions");
        var results = new XElement(ns + "Results");
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var id = $"test-{index}";
            var method = new XElement(ns + "TestMethod", new XAttribute("name", "Case"));
            if (item.Class is not null) method.Add(new XAttribute("className", item.Class));
            definitions.Add(new XElement(ns + "UnitTest", new XAttribute("id", id), method));
            results.Add(new XElement(ns + "UnitTestResult", new XAttribute("testId", id),
                new XAttribute("testName", item.Display ?? $"{item.Class}.Case"), new XAttribute("outcome", item.Outcome)));
        }
        new XDocument(new XElement(ns + "TestRun", definitions, results)).Save(path);
        return path;
    }

    private static string RecordPath(string root) =>
        Path.Combine(root, ".orchestrator", "test-reuse-shadow", Goal.Value, "attempt-one.json");

    private static JsonDocument ReadRecord(string root)
    {
        Assert.True(File.Exists(RecordPath(root)), $"Missing shadow record: {RecordPath(root)}");
        return JsonDocument.Parse(File.ReadAllText(RecordPath(root)));
    }

    private static void AssertRow(JsonElement record, string className, string check, string decision, string reason, string outcome)
    {
        var row = Assert.Single(record.GetProperty("classes").EnumerateArray().Where(row =>
            row.GetProperty("class").GetString() == className && row.GetProperty("check").GetString() == check));
        Assert.Equal(decision, row.GetProperty("decision").GetString());
        Assert.Equal(reason, row.GetProperty("reason").GetString());
        Assert.Equal(outcome, row.GetProperty("outcome").GetString());
    }

    private static int Summary(JsonElement record, string key) => record.GetProperty("summary").GetProperty(key).GetInt32();
    private static string[] FailedClasses(JsonElement record) => record.GetProperty("summary")
        .GetProperty("failed_would_skip_classes").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static void AssertSameReceipt(AcceptanceCheckResult? actual, AcceptanceCheckResult? expected)
    {
        Assert.NotNull(actual);
        Assert.NotNull(expected);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Passed, actual.Passed);
        Assert.Equal(expected.Advisory, actual.Advisory);
        Assert.Equal(expected.ResultSummary, actual.ResultSummary);
    }

    private static void AssertSameReuse(string root, string baseline, AcceptanceManifestCheck check,
        Func<string, string, string, IReadOnlyList<string>> resolver, bool expectReuse = true)
    {
        var cache = CreateCache(root, [check], resolver, "attempt-two");
        var control = CreateCache(baseline, [check], attempt: "attempt-two");
        var actual = cache.TryReuse(check);
        var expected = control.TryReuse(check);
        if (!expectReuse)
        {
            Assert.Null(actual);
            Assert.Null(expected);
            Assert.Equal(control.Misses, cache.Misses);
            return;
        }
        Assert.NotNull(actual);
        Assert.NotNull(expected);
        Assert.Equal(expected.Passed, actual.Passed);
        Assert.Equal(expected.ResultSummary, actual.ResultSummary);
        Assert.Equal(expected.TestResultAttemptId, actual.TestResultAttemptId);
        Assert.Equal(expected.TestResultPaths, actual.TestResultPaths);
        Assert.Equal(expected.TestResultIsExplicitCrossAttemptReuse, actual.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal(control.Misses, cache.Misses);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record TrxCase(string? Class, string Outcome = "Passed", string? Display = null);
}
