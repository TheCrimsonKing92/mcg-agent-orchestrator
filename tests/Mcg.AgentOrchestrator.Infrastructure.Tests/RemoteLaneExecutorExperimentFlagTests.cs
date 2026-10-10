using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its configuration file and durable stores in a unique root.
public sealed class RemoteLaneExecutorExperimentFlagTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyAndRevert_PreserveBytesAndCapturePriorOnce(bool withBom)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var encoding = new UTF8Encoding(withBom);
        var json = ExperimentFlagTestFixture.ExecutorsJson();
        File.WriteAllText(fixture.ExecutorsPath, json, encoding);
        var original = File.ReadAllBytes(fixture.ExecutorsPath);
        var record = fixture.Add(ExperimentFlagTestFixture.ExecutorsSpec());
        var apply = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        Assert.Contains("replayed=False", fixture.Result(apply).Outcome);
        var expected = encoding.GetPreamble().Concat(encoding.GetBytes(
            json.Replace("\"mode\" : \"off\"", "\"mode\" : \"shadow\""))).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(fixture.ExecutorsPath));
        var configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(fixture.ExecutorsPath);
        Assert.Null(configuration.DisabledReason);
        Assert.Null(configuration.FocusedEvidence.FaultReason);
        Assert.Equal("shadow", configuration.FocusedEvidence.Mode);
        Assert.Equal(7, configuration.FocusedEvidence.SampleEvery);
        Assert.Equal(120, configuration.FocusedEvidence.GraceSeconds);
        Assert.False(Prior(fixture, record));

        // A fixed sentinel proves replay writes nothing without depending on elapsed time.
        var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.ExecutorsPath, sentinel);
        var replay = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Contains("replayed=True", fixture.Result(replay).Outcome);
        Assert.Equal(expected, File.ReadAllBytes(fixture.ExecutorsPath));
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(fixture.ExecutorsPath));
        Assert.False(Prior(fixture, record));

        var revert = fixture.Submit(record.Id, revert: true,
            assurance: OperatorIntentAdjudication.StewardAssurance, actor: "conductor",
            channel: "conductor-experiment-revert", actorKind: OperatorActorKind.Agent);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(revert).Status);
        Assert.Equal(original, File.ReadAllBytes(fixture.ExecutorsPath));
        var revertReplay = fixture.Submit(record.Id, revert: true,
            assurance: OperatorIntentAdjudication.StewardAssurance, actor: "conductor",
            channel: "conductor-experiment-revert", actorKind: OperatorActorKind.Agent);
        fixture.Tick();
        Assert.Contains("replayed=True", fixture.Result(revertReplay).Outcome);
        Assert.Equal(original, File.ReadAllBytes(fixture.ExecutorsPath));
    }

    [Fact]
    public void Apply_AlreadyShadow_CapturesTrueWithoutWriting()
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.ExecutorsPath, ExperimentFlagTestFixture.ExecutorsJson("shadow"));
        var original = File.ReadAllBytes(fixture.ExecutorsPath);
        var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.ExecutorsPath, sentinel);
        var record = fixture.Add(ExperimentFlagTestFixture.ExecutorsSpec());
        var intent = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(intent).Status);
        Assert.Contains("replayed=True", fixture.Result(intent).Outcome);
        Assert.True(Prior(fixture, record));
        Assert.Equal(original, File.ReadAllBytes(fixture.ExecutorsPath));
        Assert.Equal(sentinel, File.GetLastWriteTimeUtc(fixture.ExecutorsPath));
    }

    [Theory]
    [InlineData("missing", "executors-file-missing")]
    [InlineData("mode-invalid", "executors-file-invalid")]
    [InlineData("mode-non-string", "executors-file-invalid")]
    [InlineData("sample-invalid", "executors-file-invalid")]
    [InlineData("structure-invalid", "executors-file-invalid")]
    [InlineData("duplicate-mode", "executors-file-invalid")]
    [InlineData("duplicate-block", "executors-file-invalid")]
    [InlineData("block-absent", "focused-evidence-mode-absent")]
    [InlineData("mode-absent", "focused-evidence-mode-absent")]
    public void Apply_RefusalPreservesBytesAndDoesNotCapturePrior(string fault, string reason)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var json = ExperimentFlagTestFixture.ExecutorsJson();
        json = fault switch
        {
            "mode-invalid" => ExperimentFlagTestFixture.ExecutorsJson("bogus"),
            "mode-non-string" => json.Replace("\"mode\" : \"off\"", "\"mode\" : true"),
            "sample-invalid" => json.Replace("\"sampleEvery\":7", "\"sampleEvery\":0"),
            "structure-invalid" => json.Replace("\"lanes\": [\"lane-a\"]", "\"lanes\": null"),
            "duplicate-mode" => json.Replace("\"mode\" : \"off\"", "\"mode\" : \"off\", \"mode\":\"shadow\""),
            "duplicate-block" => json.Replace("\"focusedEvidence\" :", "\"focusedEvidence\": {}, \"focusedEvidence\" :"),
            "block-absent" => "{\"executors\":[],\"lanes\":[],\"unknown\":1}\r\n",
            "mode-absent" => json.Replace("\"mode\" : \"off\", ", ""),
            _ => json
        };
        if (fault != "missing") File.WriteAllText(fixture.ExecutorsPath, json, new UTF8Encoding(true));
        var original = File.Exists(fixture.ExecutorsPath) ? File.ReadAllBytes(fixture.ExecutorsPath) : null;
        var record = fixture.Add(ExperimentFlagTestFixture.ExecutorsSpec());
        var intent = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        Assert.Contains(reason, fixture.Result(intent).Outcome);
        Assert.Null(Prior(fixture, record));
        if (original is null) Assert.False(File.Exists(fixture.ExecutorsPath));
        else Assert.Equal(original, File.ReadAllBytes(fixture.ExecutorsPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.ExecutorsPath)!, "*.tmp"));
        if (fault is "block-absent" or "mode-absent")
            Assert.False(RemoteLaneExecutorFlags.Read(fixture.ExecutorsPath, "focusedEvidenceShadow"));
        else if (fault is not ("duplicate-mode" or "duplicate-block"))
            Assert.Null(RemoteLaneExecutorFlags.Read(fixture.ExecutorsPath, "focusedEvidenceShadow"));
    }

    [Theory]
    [InlineData(false, "conductor", "conductor-experiment-revert", OperatorActorKind.Agent, false)]
    [InlineData(true, "other", "conductor-experiment-revert", OperatorActorKind.Agent, false)]
    [InlineData(true, "conductor", "cli", OperatorActorKind.Agent, false)]
    [InlineData(true, "conductor", "conductor-experiment-revert", OperatorActorKind.Human, false)]
    [InlineData(true, "conductor", "conductor-experiment-revert", OperatorActorKind.Agent, true)]
    public void StewardAssurance_AllowsOnlyBoundConductorRevert(bool revert, string actor,
        string channel, OperatorActorKind actorKind, bool allowed)
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.ExecutorsPath, ExperimentFlagTestFixture.ExecutorsJson());
        var record = fixture.Add(ExperimentFlagTestFixture.ExecutorsSpec());
        var apply = fixture.Submit(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, fixture.Result(apply).Status);
        var before = File.ReadAllBytes(fixture.ExecutorsPath);
        var intent = fixture.Submit(record.Id, revert, OperatorIntentAdjudication.StewardAssurance,
            actor: actor, channel: channel, actorKind: actorKind);
        fixture.Tick();
        Assert.Equal(allowed ? OperatorIntentStatus.Applied : OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        if (allowed) Assert.False(fixture.Flags.Read(record.Spec.Intervention.FlagTarget!));
        else
        {
            Assert.Contains("steward-capability-boundary", fixture.Result(intent).Outcome);
            Assert.Equal(before, File.ReadAllBytes(fixture.ExecutorsPath));
        }
    }

    [Fact]
    public void Apply_BelowMutate_RefusesWithoutCaptureOrWrite()
    {
        using var fixture = new ExperimentFlagTestFixture();
        File.WriteAllText(fixture.ExecutorsPath, ExperimentFlagTestFixture.ExecutorsJson());
        var before = File.ReadAllBytes(fixture.ExecutorsPath);
        var record = fixture.Add(ExperimentFlagTestFixture.ExecutorsSpec());
        var intent = fixture.Submit(record.Id, assurance: "unverified");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, fixture.Result(intent).Status);
        Assert.Contains("tier-below-mutate", fixture.Result(intent).Outcome);
        Assert.Equal(before, File.ReadAllBytes(fixture.ExecutorsPath));
        Assert.Null(Prior(fixture, record));
    }

    [Fact]
    public void Add_AcceptsRemoteFileKindAndRoundTripsTarget()
    {
        using var fixture = new ExperimentFlagTestFixture();
        var path = Path.Combine(fixture.Root, "flag-spec.json");
        var json = JsonSerializer.Serialize(ExperimentFlagTestFixture.ExecutorsSpec(), ExperimentStore.JsonOptions);
        Assert.Contains("\"remote-lane-executors\"", json);
        File.WriteAllText(path, json);
        ExecuteAdd(fixture.Workspace, path);
        var records = fixture.Experiments.ListAllAsync().GetAwaiter().GetResult();
        var target = Assert.Single(records).Spec.Intervention.FlagTarget!;
        Assert.Equal(ExperimentFlagFileKind.RemoteLaneExecutors, target.FileKind);
        Assert.Equal("focusedEvidenceShadow", target.PropertyName);
        Assert.True(target.ValueToApply);
        Assert.Null(target.PriorValue);
    }

    [Theory]
    [InlineData("followerGatesEnabled", ExperimentFlagFileKind.RemoteLaneExecutors, null)]
    [InlineData("sampleEvery", ExperimentFlagFileKind.RemoteLaneExecutors, null)]
    [InlineData("FocusedEvidenceShadow", ExperimentFlagFileKind.RemoteLaneExecutors, null)]
    [InlineData("focusedEvidenceShadow", ExperimentFlagFileKind.ConductorPolicy, null)]
    [InlineData("focusedEvidenceShadow", ExperimentFlagFileKind.RemoteLaneExecutors, false)]
    public void Add_RejectsWrongPropertyOrSuppliedPriorWithoutWriting(string property,
        ExperimentFlagFileKind kind, bool? prior)
    {
        using var fixture = new ExperimentFlagTestFixture();
        var before = File.ReadAllBytes(fixture.Workspace.ExperimentStorePath);
        var path = Path.Combine(fixture.Root, "flag-spec.json");
        File.WriteAllText(path, JsonSerializer.Serialize(ExperimentFlagTestFixture.Spec(
            new(kind, property, true, prior)), ExperimentStore.JsonOptions));
        Assert.Throws<ArgumentException>(() => ExecuteAdd(fixture.Workspace, path));
        Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.ExperimentStorePath));
        Assert.Equal(0, fixture.Experiments.CountAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void FlagReadersAndControllers_HaveNoWriter()
    {
        var root = RepositoryRoot();
        foreach (var path in new[]
        {
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/RemoteLaneExecutorFlags.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/ExperimentFlagStateReader.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorExperimentFlagKeepController.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorExperimentFlagRevertController.cs"
        })
        {
            var source = File.ReadAllText(Path.Combine(root, path));
            foreach (var forbidden in new[] { "File.Write", "File.Move", "FileStream", "partial class" })
                Assert.DoesNotContain(forbidden, source);
        }
    }

    private static void ExecuteAdd(OrchestratorWorkspace workspace, string path)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        AsyncLocalConsoleRouter.Capture(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
            ["experiment-add", "--spec", path], new AgentOrchestratorKernel(), workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal)));
    }

    private static bool? Prior(ExperimentFlagTestFixture fixture, ExperimentRecord record) =>
        fixture.Experiments.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Spec.Intervention.FlagTarget!.PriorValue;

    private static string RepositoryRoot([CallerFilePath] string source = "") => VerifiedRepositoryRoot.Find(source);
}
