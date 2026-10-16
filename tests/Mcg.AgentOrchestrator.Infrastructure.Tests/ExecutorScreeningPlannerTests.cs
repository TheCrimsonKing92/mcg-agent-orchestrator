using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

public sealed class ExecutorScreeningPlannerTests
{
    [Fact]
    public void LaneSelectionRejectsHostKeysAndBuildChangesWithMachineLocalNegativeControl()
    {
        var settings = AcceptanceGateEngineSettings.Parse("""
            {"engine":{"infrastructureTestLanes":[
              {"name":"Free","filter":"FullyQualifiedName~Free"},
              {"name":"Host","filter":"FullyQualifiedName~Host","exclusiveResourceKeys":["host:Fixture"]},
              {"name":"Build","filter":"FullyQualifiedName~Build","exclusiveResourceKeys":["xunit:EnvMutation"],"requiresBuildSystemChange":true},
              {"name":"Accounting","filter":"FullyQualifiedName~Accounting","exclusiveResourceKeys":["xunit:JobAccounting"]}
            ]}}
            """);
        using var fixture = new Fixture();
        var config = fixture.Configuration with { MachineLocalResourceKeys = ["xunit:EnvMutation", "xunit:ProcessSpawning"] };
        var selection = ExecutorScreeningPlanner.SelectLanes(settings.InfrastructureTestLanes, config);
        Assert.Equal("Free", Assert.Single(selection.Selected).Name);
        Assert.Equal(new[] { "exclusive-key:host:Fixture", "build-system-change", "exclusive-key:xunit:JobAccounting" },
            selection.Excluded.Select(lane => lane.Reason));
        var control = ExecutorScreeningPlanner.SelectLanes(settings.InfrastructureTestLanes, config with
        { MachineLocalResourceKeys = [.. config.MachineLocalResourceKeys, "xunit:JobAccounting"] });
        Assert.Equal(new[] { "Free", "Accounting" }, control.Selected.Select(lane => lane.Name));
    }

    [Fact]
    public void SelectionAgreesWithCoordinatorForEveryRealManifestLane()
    {
        using var fixture = new Fixture();
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
        Assert.NotEmpty(lanes);
        var config = fixture.Configuration with
        { MachineLocalResourceKeys = ["xunit:ProcessSpawning", "xunit:EnvMutation", "xunit:ProcessSpawningProcessLocal", "xunit:DotnetBuildSlots"] };
        var selection = ExecutorScreeningPlanner.SelectLanes(lanes, config);
        config = config.ResolveLanes(lanes.Select(lane => "infrastructure tests: " + lane.Name));
        using var coordinator = new RemoteLaneCoordinator(config,
            new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
            fixture.Root, null, fixture.Executor, fixture.Clock, TimeSpan.FromMilliseconds(1), null);
        foreach (var lane in lanes)
        {
            var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "infrastructure tests: " + lane.Name, Type = "dotnet-test", Project = ExecutorScreeningPlanner.Project,
                Arguments = ["--filter", lane.Filter], ExclusiveResourceKeys = lane.ExclusiveResourceKeys,
                RequiresBuildSystemChange = lane.RequiresBuildSystemChange
            };
            Assert.Equal(coordinator.IsEligible(check), selection.Selected.Contains(lane));
        }
    }

    [Fact]
    public void SelectionKeepsConfigurationPartitionAndKeylessSpecialCase()
    {
        using var fixture = new Fixture();
        var lanes = new[] { new AcceptanceTestLane("Free", "A") { RequiresBuildSystemChange = true }, new AcceptanceTestLane("Blank", "") };
        var selected = ExecutorScreeningPlanner.SelectLanes(lanes, fixture.Configuration);
        Assert.Equal("Free", Assert.Single(selected.Selected).Name);
        Assert.Equal("not-infrastructure", Assert.Single(selected.Excluded).Reason);
        var control = ExecutorScreeningPlanner.SelectLanes(lanes, fixture.Configuration with { AllInfrastructureLanes = false, Lanes = [] });
        Assert.Empty(control.Selected);
        Assert.All(control.Excluded, lane => Assert.Equal("not-configured", lane.Reason));
    }

    [Fact]
    public void CandidateChoiceOrdersEligibleDeveloperCompletionsAndDeduplicatesOnlyCurrentCommits()
    {
        using var fixture = new Fixture();
        var basis = fixture.Candidate;
        var candidates = new[]
        {
            basis with { GoalId = "verified", Status = GoalStatus.Verified, CommittedAt = At(10) },
            basis with { GoalId = "active", CommittedAt = At(9) },
            basis with { GoalId = "pending", DevelopersComplete = false, CommittedAt = At(1) },
            basis with { GoalId = "verifying", Status = GoalStatus.Verifying, CommittedAt = At(1) },
            basis with { GoalId = "dirty", WorktreeClean = false, CommittedAt = At(1) },
            basis with { GoalId = "main", Commit = basis.MainSha, CommittedAt = At(1) },
            basis with { GoalId = "screened", CommittedAt = At(1) },
            basis with { GoalId = "new-commit", CommittedAt = At(11) }
        };
        var receipts = new List<ExecutorScreeningReceipt> { Receipt(candidates[6]), Receipt(candidates[7] with { Commit = "old" }) };
        foreach (var expected in new[] { "active", "verified", "new-commit" })
        {
            var choice = Assert.IsType<ExecutorScreeningCandidate>(ExecutorScreeningPlanner.PickCandidate(candidates, receipts));
            Assert.Equal(expected, choice.GoalId);
            receipts.Add(Receipt(choice));
        }
        Assert.Null(ExecutorScreeningPlanner.PickCandidate(candidates, receipts));
        Assert.Equal("a", ExecutorScreeningPlanner.PickCandidate(
            [basis with { GoalId = "z" }, basis with { GoalId = "a" }], [])!.GoalId);
    }

    [Fact]
    public void ExecutorChoiceSkipsNonSshAndOccupiedAndHonorsTheRequestedId()
    {
        var entries = new[] { new RemoteLaneExecutorEntry("local", 60), new("busy", 60, "ssh"),
            new("first", 60, "ssh"), new("last", 60, "ssh") };
        Assert.Equal("first", ExecutorScreeningPlanner.PickIdleExecutor(entries, id => id == "busy")!.Id);
        Assert.Null(ExecutorScreeningPlanner.PickIdleExecutor(entries, _ => true));
        Assert.Equal("last", ExecutorScreeningPlanner.PickIdleExecutor(entries, _ => false, "last")!.Id);
        Assert.Null(ExecutorScreeningPlanner.PickIdleExecutor(entries, id => id == "busy", "busy"));
    }

    private static DateTimeOffset At(int hour) => new(2026, 10, 16, hour, 0, 0, TimeSpan.Zero);
    private static ExecutorScreeningReceipt Receipt(ExecutorScreeningCandidate candidate) =>
        new(Guid.NewGuid().ToString("N"), candidate.GoalId, candidate.Commit, candidate.Tree, candidate.MainSha,
            "laptop", At(12), At(13), ExecutorScreeningVerdict.Incomplete, [], []);
}
