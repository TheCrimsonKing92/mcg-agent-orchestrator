using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerProtectedApprovalSatisfiedTests : IDisposable
{
    private const string Manifest = "config/acceptance-manifest.json";
    private const string Main = """{"engine":{"maxConcurrentShards":2}}""";
    private const string Candidate = """{"engine":{"maxConcurrentShards":3}}""";
    private const string EquivalentMain = """
        {"version":1,"engine":{"maxConcurrentShards":2,"infrastructureTestLanes":[
          {"name":"Spawn","filter":"FullyQualifiedName~Alpha|FullyQualifiedName~Beta"},
          {"name":"Remainder","filter":"FullyQualifiedName~Other"}],
          "localTestPartitions":[{"name":"P","laneNames":["Spawn","Remainder"]}]}}
        """;
    private const string EquivalentCandidate = """
        {"version":1,"engine":{"maxConcurrentShards":2,"infrastructureTestLanes":[
          {"name":"Spawn","filter":"FullyQualifiedName~Alpha"},
          {"name":"Local","filter":"FullyQualifiedName~Beta","ownedCollections":["LocalCol"],"exclusiveResourceKeys":["xunit:LocalCol"]},
          {"name":"Remainder","filter":"FullyQualifiedName~Other"}],
          "localTestPartitions":[{"name":"P","laneNames":["Spawn","Local","Remainder"]}]}}
        """;
    private static readonly string MemberSha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);
    private static readonly string CombinedSha = new('c', 40);
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Theory]
    [Xunit.InlineData(true, OperatorActorKind.Human)]
    [Xunit.InlineData(false, OperatorActorKind.Human)]
    [Xunit.InlineData(true, OperatorActorKind.Agent)]
    public async Task SingleGoalFlagRequiresHumanApprovalForCandidate(bool sameCandidate, OperatorActorKind actor)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Manifest approval", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var decisions = CollaborationItemStore.ForDirectory(_root);
        await ApproveAsync(kernel, goal, decisions, sameCandidate ? MemberSha : OtherSha, actor);
        WriteCandidate(Candidate);
        var result = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, goal.Id,
            [Manifest], null, (_, args) => args[0] == "diff" ? Manifest + "\n" : Main,
            decisions, () => throw new InvalidDataException("No equivalent partition"), MemberSha);
        Assert.Equal(sameCandidate && actor == OperatorActorKind.Human, result.OwnerApprovalSatisfied);
        Assert.Equal(!(sameCandidate && actor == OperatorActorKind.Human), result.Failure is not null);
    }

    [Xunit.Fact]
    public void NoOwnerProtectedChangeDoesNotAssertOwnerApproval()
    {
        var result = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, GoalId.New(),
            ["src/Sample.cs"], null, (_, _) => throw new InvalidOperationException("No protected diff should be read"),
            null, Inventory, MemberSha);
        Assert.Null(result.Failure);
        Assert.Null(result.Pass);
        Assert.False(result.OwnerApprovalSatisfied);
    }

    [Xunit.Fact]
    public void PartitionEquivalentPassDoesNotAssertOwnerApproval()
    {
        WriteCandidate(EquivalentCandidate);
        var result = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            [Manifest], null, (_, args) => args[0] == "diff" ? Manifest + "\n" : EquivalentMain,
            CollaborationItemStore.ForDirectory(_root), Inventory, MemberSha);
        Assert.Null(result.Failure);
        Assert.Equal("partition-equivalent", Assert.IsType<AcceptanceCheckResult>(result.Pass).ResultSummary);
        Assert.False(result.OwnerApprovalSatisfied);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public async Task CohortFlagDistinguishesApprovedManifestMemberFromPartitionEquivalence(bool approve, bool equivalent)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Manifest member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var other = kernel.CreateGoal("Other member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var decisions = CollaborationItemStore.ForDirectory(_root);
        if (approve) await ApproveAsync(kernel, goal, decisions, MemberSha, OperatorActorKind.Human);
        var main = equivalent ? EquivalentMain : Main;
        var candidate = equivalent ? EquivalentCandidate : Candidate;
        WriteCandidate(candidate);
        string? Git(string _, string[] args) => args[0] switch
        {
            "show" => args[1] == "main:" + Manifest ? main :
                args[1] == MemberSha + ":" + Manifest || args[1] == "HEAD:" + Manifest ? candidate : null,
            "diff" => args[3] == "main...HEAD" ? Manifest + "\nREADME.md\n" :
                args[3] == "main..." + MemberSha ? Manifest + "\n" :
                args[3] == "main..." + OtherSha ? "README.md\n" : null,
            _ => null
        };
        var result = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            [Manifest, "README.md"],
            [new(goal.Id, MemberSha), new(other.Id, OtherSha)], Git, decisions, Inventory, CombinedSha);
        Assert.Null(result.Failure);
        Assert.Equal(approve, result.OwnerApprovalSatisfied);
        if (equivalent) Assert.Equal("partition-equivalent", result.Pass?.ResultSummary);
    }

    [Xunit.Fact]
    public async Task CombinedCandidateApprovalDoesNotSubstituteForManifestMemberApproval()
    {
        var kernel = new AgentOrchestratorKernel();
        var combined = kernel.CreateGoal("Combined candidate", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var member = kernel.CreateGoal("Manifest member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var decisions = CollaborationItemStore.ForDirectory(_root);
        await ApproveAsync(kernel, combined, decisions, CombinedSha, OperatorActorKind.Human);
        WriteCandidate(Candidate);
        var result = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, combined.Id,
            [Manifest], [new(member.Id, MemberSha)],
            (_, args) => args[0] == "diff" ? Manifest + "\n" : args[1] == "main:" + Manifest ? Main : Candidate,
            decisions, Inventory, CombinedSha);
        // The existing combined-approval pass stays intact; only the new predicate is narrower.
        Assert.Null(result.Failure);
        Assert.False(result.OwnerApprovalSatisfied);
    }

    [Xunit.Fact]
    public async Task CohortApprovalWithoutManifestChangeDoesNotAssertManifestOwnerApproval()
    {
        const string policy = "config/conductor-policy.json";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Policy member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var decisions = CollaborationItemStore.ForDirectory(_root);
        await ApproveAsync(kernel, goal, decisions, MemberSha, OperatorActorKind.Human);
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, policy), "new-policy");
        var result = GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            [policy], [new(goal.Id, MemberSha)],
            (_, args) => args[0] == "diff" ? policy + "\n" :
                args[1].StartsWith("main:", StringComparison.Ordinal) ? "old-policy" : "new-policy",
            decisions, Inventory, CombinedSha);
        Assert.Null(result.Failure);
        Assert.False(result.OwnerApprovalSatisfied);
    }

    private async Task ApproveAsync(AgentOrchestratorKernel kernel, Goal goal,
        ICollaborationItemStore decisions, string sha, OperatorActorKind actor)
    {
        var intents = new SqliteOperatorIntentStore(Path.Combine(_root, "operator-intents.db"), Path.Combine(_root, "logs"));
        await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, goal.Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(sha, "Owner approved candidate.")),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: actor));
        new OperatorIntentCoordinator(intents, decisions: decisions, goalStateVersionResolver: _ => 0).ExecutePending(kernel, goal);
    }

    private void WriteCandidate(string manifest)
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.WriteAllText(Path.Combine(_root, "config", "acceptance-manifest.json"), manifest);
    }

    private static AcceptanceTestInventory Inventory() => new(
        [new("Tests.Alpha", "SpawnCol"), new("Tests.Beta", "LocalCol"), new("Tests.Other", null)],
        new HashSet<string>(["SpawnCol", "LocalCol"], StringComparer.Ordinal),
        new HashSet<string>(["LocalCol"], StringComparer.Ordinal));

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
