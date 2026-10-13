using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceAttributionReplayRequestTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("actual-copy", null)]
    [InlineData(null, "{}")]
    [InlineData("normalized", "{}")]
    [InlineData("actual-copy", "{}")]
    public void ActualModeMissingOrMalformedRequestFailsClosed(string? mode, string? request) =>
        Assert.Throws<InvalidOperationException>(() => AcceptanceAttributionReplayFixture.ValidateRequest(mode, request));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiedReplayPublishesBoundArtifactWithHistoricalAndFinalIdentities(bool historicalSummary)
    {
        using var fixture = new CopiedFixture(historicalSummary);
        var receipt = AcceptanceAttributionReplayFixture.ReplayCopiedAttempt("actual-copy", fixture.RequestJson());
        Assert.Equal(1, receipt.FailureDecisions);
        Assert.True(receipt.NextGoalAdvanced);
        Assert.Equal(historicalSummary ? 0 : 1, receipt.ProductFailureRecords);
        Assert.Equal(historicalSummary
            ? "landing-completion:Escalate:unmet-criteria-no-retry-task"
            : "landing-completion:Hold:environmental-apparatus-hold", receipt.CompletionRoute);
        if (historicalSummary)
            Assert.IsType<Mcg.AgentOrchestrator.Core.Conductor.ConductorAdvanceOutcome.Escalated>(receipt.Outcome);
        else
            Assert.IsType<Mcg.AgentOrchestrator.Core.Conductor.ConductorAdvanceOutcome.Held>(receipt.Outcome);
        using var report = JsonDocument.Parse(File.ReadAllText(fixture.Request.ResultPath));
        var root = report.RootElement;
        Assert.Equal("actual-copy", root.GetProperty("replayKind").GetString());
        Assert.Equal(fixture.Request.InputSha256, root.GetProperty("request").GetProperty("inputSha256").GetString());
        Assert.Equal(fixture.Request.FinalCandidateSha, root.GetProperty("request").GetProperty("finalCandidateSha").GetString());
        Assert.Equal(fixture.Request.AssemblySha256, root.GetProperty("request").GetProperty("assemblySha256").GetString());
        Assert.Equal(ApparatusRedGateSourceIndexTests.Fixture.Candidate, root.GetProperty("receipt").GetProperty("candidateSha").GetString());
        Assert.Equal(fixture.Input.OriginalTrx.Sha256, root.GetProperty("copiedReceipts").GetProperty("originalTrx").GetProperty("sha256").GetString());
        Assert.Equal(fixture.Request.Sources[0].Sha256, root.GetProperty("request").GetProperty("sources")[0].GetProperty("sha256").GetString());
        Assert.Equal(0, root.GetProperty("receipt").GetProperty("sourceReads").GetInt32());
        Assert.Equal(1, root.GetProperty("receipt").GetProperty("failureDecisions").GetInt32());
        Assert.Equal(receipt.CompletionRoute, root.GetProperty("receipt").GetProperty("completionRoute").GetString());
        Assert.Equal(receipt.ProductFailureRecords, root.GetProperty("receipt").GetProperty("productFailureRecords").GetInt32());
        Assert.NotEqual(root.GetProperty("originalTrxRunId").GetString(), root.GetProperty("rerunTrxRunId").GetString());
    }

    [Theory]
    [InlineData("input-hash")]
    [InlineData("candidate")]
    [InlineData("root")]
    [InlineData("source")]
    [InlineData("assembly")]
    [InlineData("copy")]
    public void MismatchedInputCandidateSourceAssemblyOrCopiedReceiptPublishesNoEvidence(string mismatch)
    {
        using var fixture = new CopiedFixture();
        switch (mismatch)
        {
            case "input-hash": File.AppendAllText(fixture.Request.InputPath, " "); break;
            case "candidate": fixture.WriteInput(fixture.Input with { FinalCandidateSha = new string('b', 40) }); break;
            case "root": fixture.WriteInput(fixture.Input with { SourceRoot = Path.Combine(fixture.Source.Root, "other") }); break;
            case "source": File.AppendAllText(Path.Combine(fixture.Source.Root, fixture.Request.Sources[0].File), "// changed"); break;
            case "assembly": fixture.Request = fixture.Request with { AssemblySha256 = new string('0', 64) }; break;
            case "copy": File.AppendAllText(Path.Combine(fixture.Source.Root, fixture.Input.OriginalTrx.File), " "); break;
        }
        Assert.Throws<InvalidOperationException>(() => AcceptanceAttributionReplayFixture.ReplayCopiedAttempt("actual-copy", fixture.RequestJson()));
        Assert.False(File.Exists(fixture.Request.ResultPath));
    }

    private sealed class CopiedFixture : IDisposable
    {
        internal ApparatusRedGateSourceIndexTests.Fixture Source { get; } = new();
        internal AcceptanceAttributionReplayFixture.CopiedAttemptInput Input { get; }
        internal AcceptanceAttributionReplayFixture.ReplayRequest Request { get; set; }
        private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        internal CopiedFixture(bool historicalSummary = false)
        {
            Source.WriteSource("tests/A/A.cs", "class A {}");
            var check = Source.CleanupCheck(historicalSummary ? 9532 : 2, historicalSummary ? 9529 : 1,
                historicalSummary ? 3 : 0) with { TestResultRunOrdinal = 0 };
            if (historicalSummary)
                check = check with { FailureClassification = AcceptanceFailureClassifications.SharedGateApparatusInvalidated };
            var rerun = Source.WriteTrx(0, historicalSummary ? 9529 : 1, historicalSummary ? 3 : 0, "rerun");
            var attempt = new ConductorParallelAcceptanceAttempt(ApparatusRedGateSourceIndexTests.Fixture.Attempt,
                Source.Goal.Id.Value, "replay", 0, ApparatusRedGateSourceIndexTests.Fixture.Candidate,
                ApparatusRedGateSourceIndexTests.Fixture.Main, Source.Now, Source.Now, 1,
                ConductorParallelAcceptanceAttemptOutcome.Running, "", "", "", "", "", "", ScopePaths: ["src/Changed.cs"]);
            // The historical aggregate is identityless and has no environmental classification.
            // Keep that evidence intact rather than rewriting it to select the normalized hold route.
            var summary = historicalSummary
                ? Source.Summary(check, new AcceptanceCheckResult("infrastructure tests", false, 1, "aggregate failed",
                    CoveredBy: [check.Name]))
                : Source.Summary(check);
            var artifact = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null,
                summary, attempt.BranchHeadSha, attempt.MainHeadSha, null, null);
            var heartbeat = new GateHeartbeatSnapshot(attempt.GoalId, "tests", check.Name, 0, 1, 2, "completed",
                Source.Now, Source.Now, Source.Now, 0, 0, 0, ExitCode: 2, RunId: attempt.AttemptId);
            Input = new(Source.Root, new string('a', 40), Write("attempt.json", attempt), Write("result.json", artifact),
                Copy(check.TestResultPaths![0]), Copy(rerun), Write("original-heartbeat.json", heartbeat),
                Write("rerun-heartbeat.json", heartbeat with { ChildPid = 3, ExitCode = 0 }));
            var inputPath = Path.Combine(Source.Root, "input.json");
            File.WriteAllText(inputPath, JsonSerializer.Serialize(Input, _options));
            Request = new(1, Guid.NewGuid().ToString("N"), "actual-copy", inputPath, Hash(inputPath), Source.Root,
                Input.FinalCandidateSha, Hash(typeof(AcceptanceAttributionReplayFixture).Assembly.Location),
                [Copy(Path.Combine(Source.Root, "tests/A/A.cs"))], Path.Combine(Source.Root, "report.json"));
        }
        internal void WriteInput(AcceptanceAttributionReplayFixture.CopiedAttemptInput input)
        {
            File.WriteAllText(Request.InputPath, JsonSerializer.Serialize(input, _options));
            Request = Request with { InputSha256 = Hash(Request.InputPath) };
        }
        internal string RequestJson() => JsonSerializer.Serialize(Request, _options);
        private AcceptanceAttributionReplayFixture.CopiedFile Write<T>(string name, T value)
        {
            var path = Path.Combine(Source.Root, name);
            File.WriteAllText(path, JsonSerializer.Serialize(value, _options));
            return Copy(path);
        }
        private AcceptanceAttributionReplayFixture.CopiedFile Copy(string path) => new(Path.GetRelativePath(Source.Root, path), Hash(path));
        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        public void Dispose() => Source.Dispose();
    }
}
