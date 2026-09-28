using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerProtectedCohortApprovalTests : IDisposable
{
    private const string Main = """{"engine":{"maxConcurrentShards":2}}""";
    private const string Candidate = """{"engine":{"maxConcurrentShards":3}}""";
    private const string Manifest = "config/acceptance-manifest.json";
    private static readonly string MemberSha = new('a', 40);
    private static readonly string OtherSha = new('b', 40);
    private static readonly string CombinedSha = new('c', 40);
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Fact]
    public async Task CohortHonorsApprovalForManifestChangingMemberOnly()
    {
        var kernel = new AgentOrchestratorKernel();
        var memberA = kernel.CreateGoal("Manifest member", [new TaskSpec(TaskId.New(), "Change manifest", AgentRole.Developer)]);
        var memberB = kernel.CreateGoal("Other member", [new TaskSpec(TaskId.New(), "Change readme", AgentRole.Developer)]);
        var members = new[]
        {
            new AcceptanceOwnerProtectedCohortMember(memberA.Id, MemberSha),
            new AcceptanceOwnerProtectedCohortMember(memberB.Id, OtherSha)
        };
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), Candidate);
        var decisions = CollaborationItemStore.ForDirectory(_root);

        var unapproved = Evaluate(members, decisions);
        Xunit.Assert.Equal("operator review required", Xunit.Assert.IsType<AcceptanceCheckResult>(unapproved.Failure).ResultSummary);
        Xunit.Assert.Null(unapproved.Pass);

        var intents = new SqliteOperatorIntentStore(
            Path.Combine(_root, "operator-intents.db"), Path.Combine(_root, "logs"));
        await intents.EnqueueAsync(new OperatorIntentRecord(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, memberA.Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(MemberSha, "Owner approved member A.")),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));
        new OperatorIntentCoordinator(intents, decisions: decisions, goalStateVersionResolver: _ => 0)
            .ExecutePending(kernel, memberA);

        var approved = Evaluate(members, decisions);
        Xunit.Assert.Null(approved.Failure);
        Xunit.Assert.Null(approved.Pass);
    }

    private GoalAcceptanceVerifier.OwnerProtectedDecision Evaluate(
        IReadOnlyList<AcceptanceOwnerProtectedCohortMember> members, ICollaborationItemStore decisions) =>
        GoalAcceptanceVerifier.EvaluateOwnerProtectedConfigurationForTests(_root, null,
            [Manifest, "README.md"], members, ResolveGit, decisions,
            () => throw new InvalidDataException("Non-equivalent manifests do not need an inventory."), CombinedSha);

    private static string? ResolveGit(string _, string[] args)
    {
        if (args[0] == "show") return args[1] switch
        {
            "main:config/acceptance-manifest.json" => Main,
            var key when key == $"{MemberSha}:{Manifest}" || key == $"HEAD:{Manifest}" => Candidate,
            _ => null
        };
        if (args[0] == "diff") return args[3] switch
        {
            "main...HEAD" => $"{Manifest}\nREADME.md\n",
            var key when key == $"main...{MemberSha}" => $"{Manifest}\n",
            var key when key == $"main...{OtherSha}" => "README.md\n",
            _ => null
        };
        return null;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
