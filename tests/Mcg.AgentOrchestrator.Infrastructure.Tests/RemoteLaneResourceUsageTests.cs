using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each focused fixture owns its git repository, transport and ledger.
public sealed class RemoteLaneResourceUsageTests : RemoteFirstFocusedEvidenceFixture
{
    private static JsonElement Status(bool resources = true) => JsonSerializer.SerializeToElement(resources
        ? new Dictionary<string, object> { ["testHostCpuSeconds"] = 12.5, ["testHostPeakWorkingSetBytes"] = 4096,
            ["availableMemoryMbMin"] = 1024, ["resourceSamples"] = 3 }
        : new Dictionary<string, object>());

    [Fact]
    public void ValidStatusReadsAllFourFigures()
    {
        Assert.Equal(new RemoteLaneResourceUsage(12.5, 4096, 1024, 3), RemoteLaneResourceUsage.TryRead(Status()));
        Assert.Null(RemoteLaneResourceUsage.TryRead(Status(false)));
        Assert.Null(RemoteLaneResourceUsage.TryRead(null));
        Assert.Null(RemoteLaneResourceUsage.TryRead(JsonSerializer.SerializeToElement("not-object")));
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void InvalidFigureRejectsUsage(string field, string raw)
    {
        var node = JsonNode.Parse(Status().GetRawText())!;
        node[field] = JsonNode.Parse(raw);
        using var json = JsonDocument.Parse(node.ToJsonString());
        Assert.Null(RemoteLaneResourceUsage.TryRead(json.RootElement));
    }

    public static IEnumerable<object[]> InvalidFields()
    {
        foreach (var field in new[] { "testHostCpuSeconds", "testHostPeakWorkingSetBytes", "availableMemoryMbMin", "resourceSamples" })
            foreach (var value in new[] { "-1", "1e400", "\"NaN\"", "\"12\"", "null" }) yield return [field, value];
        yield return ["resourceSamples", "0"];
        yield return ["resourceSamples", "1.5"];
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GateRowsIncludeUsageOnlyWhenStatusSuppliesIt(bool resources)
    {
        using var scenario = new RemoteSlotInstrumentationScenarioTests.Scenario(1, resources: resources);
        Assert.True((await scenario.RunAsync()).Passed);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        AssertFields(row, resources);
        AssertSerializedFields(scenario.Ledger, row, resources);
    }

    [Theory]
    [InlineData("shadow", true)]
    [InlineData("shadow", false)]
    [InlineData("remote-first", true)]
    [InlineData("remote-first", false)]
    public async Task FocusedRowsReadLastHandleStatus(string mode, bool resources)
    {
        using var scenario = CreateScenario();
        scenario.Configure(mode: mode, sample: 1);
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var submission = scenario.Completion(request);
                return Task.FromResult(submission with { Handle = new StatusHandle(submission.Handle!, Status(resources)) });
            }
        };
        scenario.Overrides.RemoteLaneExecutorForTests = fake;
        if (mode == "remote-first")
        {
            var result = await scenario.DirectAsync([FocusedCheck()]);
            Assert.True(Assert.Single(result.Results).Passed);
            Assert.Equal(0, scenario.LocalCalls);
        }
        else
        {
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(scenario.Root, scenario.Goal, null,
                options: new(RunId: "resource-shadow", ResultsPrefix: scenario.Prefix));
            await using var shadow = RemoteFocusedEvidenceShadow.Start(scenario.Overrides, scenario.Clock, scenario.Root,
                [FocusedCheck()], scenario.Goal, (IAcceptanceRunExecutionContext)owner, default);
            await shadow.CompleteAsync([new(FocusedCheck().Name, true, 0, null)]);
        }
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Equal(mode == "shadow" ? true : (bool?)null, row.EvidenceShadow);
        AssertFields(row, resources);
        AssertSerializedFields(scenario.LedgerPath, row, resources);
    }

    internal static void AssertUsage(RemoteExecutorHealthRecord row)
    {
        Assert.Equal(12.5, row.RemoteTestHostCpuSeconds);
        Assert.Equal(4096, row.RemoteTestHostPeakWorkingSetBytes);
        Assert.Equal(1024, row.RemoteAvailableMemoryMbMin);
        Assert.Equal(3, row.RemoteResourceSamples);
    }

    private static void AssertFields(RemoteExecutorHealthRecord row, bool resources)
    {
        if (resources) AssertUsage(row);
        else
        {
            Assert.Null(row.RemoteTestHostCpuSeconds); Assert.Null(row.RemoteTestHostPeakWorkingSetBytes);
            Assert.Null(row.RemoteAvailableMemoryMbMin); Assert.Null(row.RemoteResourceSamples);
        }
    }

    private static void AssertSerializedFields(string ledger, RemoteExecutorHealthRecord row, bool resources)
    {
        var baselinePath = ledger + ".baseline";
        try
        {
            RemoteExecutorHealthLedger.Append(baselinePath, row with { RemoteTestHostCpuSeconds = null,
                RemoteTestHostPeakWorkingSetBytes = null, RemoteAvailableMemoryMbMin = null, RemoteResourceSamples = null });
            using var actual = JsonDocument.Parse(Assert.Single(File.ReadAllLines(ledger)));
            using var baseline = JsonDocument.Parse(Assert.Single(File.ReadAllLines(baselinePath)));
            var added = new[] { "remote_test_host_cpu_seconds", "remote_test_host_peak_working_set_bytes", "remote_available_memory_mb_min", "remote_resource_samples" };
            Assert.Equal(baseline.RootElement.EnumerateObject().Select(property => property.Name).Order(),
                actual.RootElement.EnumerateObject().Where(property => !resources || !added.Contains(property.Name)).Select(property => property.Name).Order());
            if (resources)
            {
                Assert.Equal(12.5, actual.RootElement.GetProperty(added[0]).GetDouble());
                Assert.Equal(4096, actual.RootElement.GetProperty(added[1]).GetDouble());
                Assert.Equal(1024, actual.RootElement.GetProperty(added[2]).GetDouble());
                Assert.Equal(3, actual.RootElement.GetProperty(added[3]).GetInt32());
            }
            else Assert.Equal(baseline.RootElement.GetRawText(), actual.RootElement.GetRawText());
        }
        finally { File.Delete(baselinePath); }
    }

    private sealed class StatusHandle(IRemoteLaneHandle inner, JsonElement status) : IRemoteLaneHandle, IRemoteLaneAttemptDiagnosticsSource
    {
        public DateTimeOffset? NewestHeartbeat => inner.NewestHeartbeat;
        public RemoteLaneResult? TryGetResult() => inner.TryGetResult();
        public void Abandon() => inner.Abandon();
        public RemoteLaneHandleDiagnostics Snapshot() => new(new(1, 0, "completed", null, null), [], status);
        public Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken token) => Task.FromResult(new RemoteLaneRunnerLogCapture(null));
    }
}
