using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class CliBuildAttributionQueryCommandTests : CliTaskQueryTestSupport
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 2, 0, 0, TimeSpan.Zero);
    private static readonly string C1 = new('1', 40), C2 = new('2', 40), Tree = new('a', 40);
    private static readonly string[] Window = ["--since", "2026-10-16T00:00:00Z", "--until", "2026-10-17T00:00:00Z"];

    [Fact]
    public void ServedForms_ClassifyAsQueryOnly_AndReadOnly()
    {
        foreach (var args in new string[][]
        {
            ["build-attribution"], ["BUILD-ATTRIBUTION"], ["build-attribution", "--json"],
            ["build-attribution", "--since", "2000-01-01T00:00:00Z"]
        })
        {
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        }
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["build-attribution", "--help"]));
    }

    [Fact]
    public void SeededSources_PrintMatchingTextAndJson_WithoutWrites()
    {
        var root = CreateTempDirectory();
        try
        {
            var storage = Path.Combine(root, "storage");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var slots = Path.Combine(storage, "build-slots");
            var workerRun = Path.Combine(storage, "goals", "bbbb2222", "artifacts", "worker-build-logs", "20261016T020000000Z-1-new");
            var legacyRun = Path.Combine(storage, "goals", "cccc3333", "artifacts-build-1", "worker-build-logs", "20261016T030000000Z-1-old");
            Write(Path.Combine(workerRun, "App.log"), "Compilation request App (net10.0)\nCompilation request Core (net10.0)\nTime Elapsed 00:01:00.00");
            Write(Path.Combine(legacyRun, "One.log"), "Compilation request One (net10.0)\nTime Elapsed 00:00:40.00");
            var reason = new BuildSlotHoldRecord("pre-review-evidence:goal-aaaa1111:attempt-a", At.AddSeconds(-100), At,
                ["App/debug@1", "Core/debug@2"]).FormatDetails();
            Write(Path.Combine(slots, "build-slot-holds.jsonl"), Journal("LEASE_HELD", reason, "P1") + "\n{torn-line\n");
            Write(Path.Combine(slots, "build-slot-holds-20261016020000.jsonl"), Journal("LEASE_HELD",
                $"holder=worker-build-check:run-r heldMs=60000 waitMs=1000 tree=wbc1:git-tree:{Tree} head={C1} configuration=Debug outcome=success", workerRun));
            Write(Path.Combine(slots, "build-slot-holds.jsonl.pending-" + new string('a', 32) + ".jsonl"),
                Journal("LEASE_WAIT_TIMEOUT", "holder=worker-build-check:run-timeout waitMs=300000", "", "timed-out"));
            Write(Path.Combine(slots, "lease.journal.jsonl"), Journal("ACCEPTANCE_SLOT_PRIORITY_WAIT", "waitMs=80000", "", "expired"));
            var attempts = Path.Combine(workspace, ".orchestrator", "pre-review-evidence-attempts", "aaaa1111");
            Write(Path.Combine(attempts, "a.attempt.json"), JsonSerializer.Serialize(new { branchHeadSha = C2, completedAt = At }));
            Write(Path.Combine(attempts, "blocked.attempt.json"), JsonSerializer.Serialize(new { completedAt = At }));
            Write(Path.Combine(attempts, "blocked.result.json"), "{\"kind\":\"fault\",\"faultKind\":\"blocked-build-slot\"}");
            var before = Snapshot(root);
            var ws = OrchestratorWorkspace.ForDirectory(workspace);
            var storageRoot = new DotnetBuildStorageRoot(storage);
            var text = CaptureConsole(() => CliBuildAttributionQueryCommand.Execute(["build-attribution", .. Window], ws, storageRoot, Resolve));
            var json = CaptureConsole(() => CliBuildAttributionQueryCommand.Execute(["build-attribution", .. Window, "--json"], ws, storageRoot, Resolve));
            Assert.Contains("holds=3 held_ms=200000 compiles=5 overall_duplicate_factor=2.0", text);
            Assert.Contains("unrecorded_tree_holds=1 unknown_compile_holds=0 unreadable_records=1 cpu=unrecorded", text);
            Assert.Contains($"{Tree}\t4\t2\t2.0\t2\t160000\tpre-review-evidence=2;worker-build-check=2", text);
            Assert.Contains("pre-review-evidence\t1\t100000\t0.5\t2\t1\t0\t1", text);
            Assert.Contains("worker-build-check\t2\t100000\t0.5\t3\t2\t301000\t1", text);
            Assert.Contains("cohort-gate\t0\t0\t0.0\t0\t1\t80000\t1", text);
            using var doc = JsonDocument.Parse(json);
            var summary = doc.RootElement.GetProperty("summary");
            Assert.Equal(3, summary.GetProperty("holds").GetInt32());
            Assert.Equal(200000, summary.GetProperty("held_ms").GetInt64());
            Assert.Equal(5, summary.GetProperty("compiles").GetInt32());
            Assert.Equal(2.0, summary.GetProperty("overall_duplicate_factor").GetDouble());
            Assert.Equal(1, summary.GetProperty("unrecorded_tree_holds").GetInt32());
            Assert.Equal(1, summary.GetProperty("unreadable_records").GetInt32());
            var tree = doc.RootElement.GetProperty("trees").EnumerateArray().Single(t => t.GetProperty("tree").GetString() == Tree);
            Assert.Equal(4, tree.GetProperty("compiles").GetInt32());
            Assert.Equal(2.0, tree.GetProperty("duplicate_factor").GetDouble());
            Assert.Equal(2, tree.GetProperty("distinct_heads").GetInt32());
            var consumers = doc.RootElement.GetProperty("consumers").EnumerateArray().ToDictionary(c => c.GetProperty("class").GetString()!);
            Assert.Equal(0.5, consumers["pre-review-evidence"].GetProperty("held_share").GetDouble());
            Assert.Equal(0.5, consumers["worker-build-check"].GetProperty("held_share").GetDouble());
            Assert.Equal(301000, consumers["worker-build-check"].GetProperty("wait_ms").GetInt64());
            foreach (var consumer in consumers.Values) Assert.Equal(1, consumer.GetProperty("timeouts").GetInt32());
            AssertUnchanged(root, before);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--since")]
    [InlineData("--since", "invalid")]
    [InlineData("--since", "2026-10-17", "--until", "2026-10-16")]
    [InlineData("--since", "2026-10-16", "--until", "2026-10-16")]
    public void InvalidArguments_PrintOnlyUsage_AndDoNotRead(params string[] flags)
    {
        var root = CreateTempDirectory();
        try
        {
            var stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() => stdout = CaptureConsole(() =>
                CliBuildAttributionQueryCommand.Execute(["build-attribution", .. flags], OrchestratorWorkspace.ForDirectory(root),
                    new DotnetBuildStorageRoot(root), _ => throw new InvalidOperationException("must not resolve"))));
            Assert.Empty(stdout);
            Assert.Equal(CliCommandHelp.BuildAttributionUsage + Environment.NewLine, stderr);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ExplicitUntil_DefaultsToPreviousDay_AndEmptyReportHasNullFactor()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var storage = new DotnetBuildStorageRoot(root);
            string Run(string[] args) => CaptureConsole(() => CliBuildAttributionQueryCommand.Execute(args, workspace, storage, Resolve));
            var json = Run(["build-attribution", "--until", "2026-10-17", "--json"]);
            Assert.Equal(Run(["build-attribution", .. Window, "--json"]), json);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("summary").GetProperty("overall_duplicate_factor").ValueKind);
            Assert.Contains("overall_duplicate_factor=n/a", Run(["build-attribution", .. Window]));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string? Resolve(string head) => head == C2 ? Tree : null;
    private static string Journal(string eventName, string reason, string path, string decision = "released") =>
        JsonSerializer.Serialize(new { version = 1, recordedAt = At, @event = eventName, processId = 1, slot = "build-0",
            leaseId = "lease", reclaimedProcessId = (int?)null, decision, reason, triggerPath = path });
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
    private static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
    private static void AssertUnchanged(string root, Dictionary<string, byte[]> before)
    {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
