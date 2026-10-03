using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each git repository and ledger is private to this instance; no process-wide state is changed.
[Collection("IsolatedProcessSpawning")]
public sealed class SemanticJudgeSuspensionTests : IDisposable
{
    private const string RecordedJudge = "recursive(fake:judge)";
    private readonly string _root = SharedTestSupport.CreateTempDirectory();

    public void Dispose()
    {
        // Git object files are read-only on Windows; retries alone cannot remove them.
        var root = new DirectoryInfo(_root);
        if (root.Exists)
        {
            root.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        SharedTestSupport.RemoveTempDirectory(_root);
    }

    [Fact]
    public void SuspendedJudgeIsNotInvokedAndRecordsSuspendedEntry()
    {
        var workspace = CreateWorkspace();
        SeedInvalidVerdicts(workspace.SemanticAcceptanceLogPath);
        var provider = new CountingJudgeProvider("Fake");
        var output = new List<string>();

        RunLanding(workspace, [provider], output.Add);

        Assert.Equal(0, provider.Calls);
        Assert.Equal(11, File.ReadLines(workspace.SemanticAcceptanceLogPath).Count());
        using var receipt = ReadLastReceipt(workspace);
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("consensus").ValueKind);
        var entry = Assert.Single(receipt.RootElement.GetProperty("judges").EnumerateArray());
        Assert.Equal(RecordedJudge, entry.GetProperty("judge").GetString());
        Assert.False(entry.GetProperty("valid").GetBoolean());
        var error = Assert.Single(entry.GetProperty("errors").EnumerateArray()).GetString();
        Assert.StartsWith("judge-suspended:", error);
        Assert.Contains("last 10 recorded verdicts", error);
        Assert.Contains("every 10th landing", error);
        Assert.Single(output.Where(line => line.Contains(RecordedJudge) && line.Contains("suspended")));
    }

    [Fact]
    public void DueProbeInvokesSuspendedJudgeOnceAndValidVerdictEndsSuspension()
    {
        var workspace = CreateWorkspace();
        SeedInvalidVerdicts(workspace.SemanticAcceptanceLogPath);
        for (var index = 0; index < 9; index++)
        {
            AppendHistory(workspace.SemanticAcceptanceLogPath, RecordedJudge, false, ["judge-suspended: prior skip"]);
        }
        var provider = new CountingJudgeProvider("Fake");

        RunLanding(workspace, [provider]);

        Assert.Equal(1, provider.Calls);
        using (var probeReceipt = ReadLastReceipt(workspace))
        {
            AssertInvokedEntry(Assert.Single(probeReceipt.RootElement.GetProperty("judges").EnumerateArray()));
        }

        RunLanding(workspace, [provider]);

        Assert.Equal(2, provider.Calls);
        Assert.Equal(21, File.ReadLines(workspace.SemanticAcceptanceLogPath).Count());
        using var nextReceipt = ReadLastReceipt(workspace);
        AssertInvokedEntry(Assert.Single(nextReceipt.RootElement.GetProperty("judges").EnumerateArray()));
    }

    [Fact]
    public void JudgeWithValidVerdictInWindowIsInvoked()
    {
        var workspace = CreateWorkspace();
        AppendHistory(workspace.SemanticAcceptanceLogPath, RecordedJudge, true, []);
        SeedInvalidVerdicts(workspace.SemanticAcceptanceLogPath, 9);
        var provider = new CountingJudgeProvider("Fake");

        RunLanding(workspace, [provider]);

        Assert.Equal(1, provider.Calls);
        using var receipt = ReadLastReceipt(workspace);
        AssertInvokedEntry(Assert.Single(receipt.RootElement.GetProperty("judges").EnumerateArray()));
    }

    [Fact]
    public void MixedPanelPreservesCatalogOrderAndInvokedReceiptShape()
    {
        var workspace = CreateWorkspace("Fake", "Other");
        SeedInvalidVerdicts(workspace.SemanticAcceptanceLogPath);
        var suspended = new CountingJudgeProvider("Fake");
        var active = new CountingJudgeProvider("Other");

        RunLanding(workspace, [suspended, active]);

        Assert.Equal(0, suspended.Calls);
        Assert.Equal(1, active.Calls);
        using var receipt = ReadLastReceipt(workspace);
        var entries = receipt.RootElement.GetProperty("judges").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(RecordedJudge, entries[0].GetProperty("judge").GetString());
        Assert.False(entries[0].GetProperty("valid").GetBoolean());
        Assert.Equal("recursive(other:judge)", entries[1].GetProperty("judge").GetString());
        AssertInvokedEntry(entries[1]);
        Assert.True(receipt.RootElement.GetProperty("consensus").GetBoolean());
    }

    [Theory]
    [InlineData(0, 0, "Invoke")]
    [InlineData(9, 0, "Invoke")]
    [InlineData(10, 0, "Suspended")]
    [InlineData(10, 8, "Suspended")]
    [InlineData(10, 9, "Probe")]
    [InlineData(10, 10, "Probe")]
    public void RecentHistoryControlsSuspensionAndProbeBoundary(int invoked, int skips, string expected)
    {
        var history = Enumerable.Repeat(new SemanticJudgeSuspension.HistoryEntry(false, true), skips)
            .Concat(Enumerable.Repeat(new SemanticJudgeSuspension.HistoryEntry(false, false), invoked)).ToList();

        Assert.Equal(expected, SemanticJudgeSuspension.Decide(history).ToString());
    }

    [Fact]
    public void InvalidProbeResetsConsecutiveSkips()
    {
        List<SemanticJudgeSuspension.HistoryEntry> history =
        [
            new(false, false),
            .. Enumerable.Repeat(new SemanticJudgeSuspension.HistoryEntry(false, true), 9),
            .. Enumerable.Repeat(new SemanticJudgeSuspension.HistoryEntry(false, false), 10)
        ];

        Assert.Equal(SemanticJudgeSuspension.Decision.Suspended, SemanticJudgeSuspension.Decide(history));
    }

    [Fact]
    public void SkippedEntriesDoNotDisplaceValidInvokedVerdictFromWindow()
    {
        List<SemanticJudgeSuspension.HistoryEntry> history =
        [
            .. Enumerable.Repeat(new SemanticJudgeSuspension.HistoryEntry(false, true), 10),
            .. Enumerable.Repeat(new SemanticJudgeSuspension.HistoryEntry(false, false), 9),
            new(true, false)
        ];

        Assert.Equal(SemanticJudgeSuspension.Decision.Invoke, SemanticJudgeSuspension.Decide(history));
    }

    [Fact]
    public void HistoryUsesAppendOrderAndExactNamesWhileIgnoringMalformedEntries()
    {
        var ledger = Path.Combine(_root, "history.jsonl");
        File.WriteAllLines(ledger,
        [
            "", "not-json", "[]", "null", "{}", "{\"judges\":{}}",
            """{"judges":[null,{}, {"judge":3,"valid":false}, {"judge":"bad","valid":"false"}]}"""
        ]);
        AppendHistory(ledger, RecordedJudge, true, [], "2099-01-01");
        SeedInvalidVerdicts(ledger);
        AppendHistory(ledger, "recursive(Fake:judge)", true, []);
        for (var index = 0; index < 9; index++)
        {
            AppendHistory(ledger, RecordedJudge, false, ["judge-suspended: skip"], "1900-01-01");
        }

        var history = SemanticJudgeSuspension.ReadHistory(ledger);

        Assert.Equal(2, history.Count);
        Assert.Equal(20, history[RecordedJudge].Count);
        Assert.True(history[RecordedJudge][0].Suspended);
        Assert.True(history[RecordedJudge][^1].Valid);
        Assert.Equal(SemanticJudgeSuspension.Decision.Probe, SemanticJudgeSuspension.Decide(history[RecordedJudge]));
        Assert.Equal(SemanticJudgeSuspension.Decision.Invoke,
            SemanticJudgeSuspension.Decide(history["recursive(Fake:judge)"]));
    }

    [Fact]
    public void OnlySinglePrefixedErrorOnInvalidEntryCountsAsSuspended()
    {
        var ledger = Path.Combine(_root, "history.jsonl");
        AppendHistory(ledger, RecordedJudge, false, ["judge-suspended: skip"]);
        AppendHistory(ledger, RecordedJudge, false, ["judge-suspended: skip", "other"]);
        AppendHistory(ledger, RecordedJudge, true, ["judge-suspended: skip"]);
        AppendHistory(ledger, RecordedJudge, false, ["other"]);

        var history = SemanticJudgeSuspension.ReadHistory(ledger)[RecordedJudge];

        Assert.Equal(new[] { false, false, false, true }, history.Select(entry => entry.Suspended));
    }

    [Fact]
    public void MissingLedgerLeavesJudgeEligible()
    {
        Assert.Empty(SemanticJudgeSuspension.ReadHistory(Path.Combine(_root, "missing.jsonl")));
        Assert.Equal(SemanticJudgeSuspension.Decision.Invoke, SemanticJudgeSuspension.Decide([]));
    }

    private OrchestratorWorkspace CreateWorkspace(params string[] providerNames)
    {
        RunGit("init", "-b", "main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(_root, "A.cs"), "class A {}\n");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial");
        RunGit("checkout", "-b", "goal/test");
        File.WriteAllText(Path.Combine(_root, "A.cs"), "class A { string Done() => \"done\"; }\n");
        RunGit("add", ".");
        RunGit("commit", "-m", "goal change");
        var workspace = OrchestratorWorkspace.ForDirectory(_root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog(
            (providerNames.Length > 0 ? providerNames : ["Fake"]).Select(provider =>
                new ModelFunctionBinding(ModelFunctionPurposes.AcceptanceJudge, ModelLane.CheapApi,
                    new ModelProfile(provider, "judge", ModelCapability.Text, SubscriptionMode.ApiKey))).ToList()));
        return workspace;
    }

    private void RunGit(params string[] args)
    {
        var result = GitCli.Run(_root, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)} failed: {result.Error}");
    }

    private void RunLanding(OrchestratorWorkspace workspace, IModelProvider[] providers, Action<string>? writeLine = null)
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Implement required behavior",
            [new TaskSpec(TaskId.New(), "Do it", AgentRole.Developer, verificationPlan: "required behavior present")]);
        GoalLandingPostActions.RunAdvisorySemanticAcceptance(goal, workspace,
            new InMemoryModelProviderRegistry(providers), WorkerProfileCatalog.Default(), _root, null, writeLine);
    }

    private static JsonDocument ReadLastReceipt(OrchestratorWorkspace workspace) =>
        JsonDocument.Parse(File.ReadLines(workspace.SemanticAcceptanceLogPath).Last());

    private static void AssertInvokedEntry(JsonElement entry)
    {
        Assert.Equal(new[] { "judge", "valid", "criteriaMet", "confidence", "reasons", "unmetCriteria", "errors" },
            entry.EnumerateObject().Select(property => property.Name));
        Assert.True(entry.GetProperty("valid").GetBoolean());
        Assert.True(entry.GetProperty("criteriaMet").GetBoolean());
        Assert.Empty(entry.GetProperty("errors").EnumerateArray());
    }

    private static void SeedInvalidVerdicts(string ledger, int count = 10)
    {
        for (var index = 0; index < count; index++)
        {
            AppendHistory(ledger, RecordedJudge, false, ["Judge produced no output."]);
        }
    }

    private static void AppendHistory(string ledger, string judge, bool valid, string[] errors, string at = "2000-01-01") =>
        File.AppendAllText(ledger, JsonSerializer.Serialize(new
        {
            at, goalId = Guid.NewGuid().ToString("n"),
            judges = new[] { new { judge, valid, errors } }
        }) + Environment.NewLine);

    private sealed class CountingJudgeProvider(string providerName) : IModelProvider
    {
        public string ProviderName => providerName;
        public int Calls { get; private set; }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ModelResponse("""
                ```json
                {"criteria_met":true,"confidence":"high","reasons":["ok"],"unmet_criteria":[]}
                ```
                """, null, "stop"));
        }
    }
}
