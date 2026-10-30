// Parallel-safe: explicit policy switches, private temporary artifacts, no processes or global state.
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

public sealed class AcceptanceGateLanePlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-plan-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlyList<AcceptanceTestLane> Lanes = AcceptanceGateEngineSettings.Parse("""
        {"checks":[],"engine":{"infrastructureTestLanes":[
          {"name":"Console","filter":"FullyQualifiedName~ConsoleTests","estimatedSerialSeconds":10},
          {"name":"Policy","filter":"FullyQualifiedName~PolicyTests","estimatedSerialSeconds":20},
          {"name":"Mtp managed project rebuild","filter":"FullyQualifiedName~RebuildTests","estimatedSerialSeconds":30,"requiresBuildSystemChange":true}
        ]}}
        """).InfrastructureTestLanes;

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleGateProgress.cs", false)]
    [InlineData("Directory.Build.props", true)]
    public void PlanPublishesTheActualSelectionInEventAndArtifact(string changedPath, bool buildLaneScheduled)
    {
        var policy = BuildPolicyShardPlan([changedPath], new(FullShards: false, ChangeScoped: true));
        var selected = SelectInfrastructureTestLanes(Lanes, [changedPath], policy);
        var checks = selected.Select(LaneCheck).ToArray();
        var plan = AcceptanceGateLanePlanPublisher.Classify("attempt", Lanes, selected,
            [BroadCheck()], [], policy, ClassifyDotnetShardDisposition([changedPath]), [], checks, checks, checks);
        Directory.CreateDirectory(_root);
        var prefix = Path.Combine(_root, "attempt");
        var events = new List<AcceptanceGateProgress>();
        AcceptanceGateLanePlanPublisher.Publish(plan, prefix, null, events.Add, TimeProvider.System);
        var emitted = Assert.Single(events);
        Assert.Equal("lane-plan", emitted.Phase);
        Assert.Equal(prefix + AcceptanceGateLanePlan.ArtifactSuffix, emitted.HeartbeatPath);
        var artifact = File.ReadAllText(emitted.HeartbeatPath);
        Assert.Equal("GATE_LANE_PLAN " + artifact, emitted.CurrentTarget);
        using var document = JsonDocument.Parse(artifact);
        var root = document.RootElement;
        Assert.Equal(AcceptanceGateLanePlan.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.Equal("attempt", root.GetProperty("attemptId").GetString());
        Assert.Equal(selected.Select(lane => lane.Name), root.GetProperty("scheduled").EnumerateArray().Select(lane => lane.GetString()));
        Assert.Equal(buildLaneScheduled ? 3 : 2, plan.Scheduled.Count);
        if (buildLaneScheduled) Assert.Empty(plan.Skipped);
        else Assert.Equal(new AcceptanceGateLaneSkip("Mtp managed project rebuild", "no-build-system-change"), Assert.Single(plan.Skipped));
        Assert.Equal(plan.Skipped.Count, root.GetProperty("skipped").GetArrayLength());
    }

    [Theory]
    [InlineData("known-empty-candidate")]
    [InlineData("docs-tree-only-candidate")]
    [InlineData("project-absent-from-candidate")]
    [InlineData("no-change-in-dependency-closure")]
    [InlineData("replaced-by-focused-check")]
    [InlineData("semantic-duplicate")]
    [InlineData("outside-attribution-scope")]
    [InlineData("outside-change-scope")]
    public void ExistingExclusionsHaveDistinctReasons(string reason)
    {
        var lane = Lanes[0];
        var check = LaneCheck(lane);
        var policy = PolicyShardPlan.Full("test", new HashSet<string>());
        var disposition = reason == "known-empty-candidate" ? DotnetShardDisposition.KnownEmptyCandidate :
            reason == "docs-tree-only-candidate" ? DotnetShardDisposition.DocsTreeOnlyCandidate : DotnetShardDisposition.RunDotnetShards;
        if (reason == "project-absent-from-candidate") policy = policy with { AbsentProjects = new HashSet<string> { InfrastructureTestsProject } };
        if (reason == "no-change-in-dependency-closure") policy = PolicyShardPlan.Scoped("test", new HashSet<string>());
        Check[] planned = reason is "outside-attribution-scope" or "outside-change-scope" ? [check] : [];
        Check[] attributed = reason == "outside-change-scope" ? [check] : [];
        Check[] required = reason == "replaced-by-focused-check" ? [check] : [];
        GoalAcceptanceVerifier.SemanticExecutionDeduplicationReceipt[] receipts = reason == "semantic-duplicate"
            ? [new("partition", "kept", check.Name, 0, 1, "hash")] : [];
        var plan = AcceptanceGateLanePlanPublisher.Classify("attempt", [lane], [lane], [BroadCheck()], required,
            policy, disposition, receipts, planned, attributed, []);
        Assert.Empty(plan.Scheduled);
        Assert.Equal(reason, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void ArtifactWriteFailureStillEmitsThePlan()
    {
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", ["Console"], []);
        var events = new List<AcceptanceGateProgress>();
        AcceptanceGateLanePlanPublisher.Publish(plan, Path.Combine(_root, "missing", "attempt"), null, events.Add, TimeProvider.System);
        Assert.Equal(plan.FormatEventTarget(), Assert.Single(events).CurrentTarget);
        Assert.False(File.Exists(events[0].HeartbeatPath));
    }

    private static Check BroadCheck() => new() { Name = "infrastructure tests", Type = "dotnet-test", Project = InfrastructureTestsProject };
    private static Check LaneCheck(AcceptanceTestLane lane) => new()
    {
        Name = "infrastructure tests: " + lane.Name, Type = "dotnet-test", Project = InfrastructureTestsProject,
        Arguments = ["--filter", lane.Filter]
    };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
