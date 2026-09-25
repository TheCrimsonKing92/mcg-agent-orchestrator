using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerProtectedPolicyDecisionTests : IDisposable
{
    private const string Trusted = """{"checks":[{"command":"old"}]}""";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Theory]
    [Xunit.InlineData(true, OperatorActorKind.Human)]
    [Xunit.InlineData(false, OperatorActorKind.Human)]
    [Xunit.InlineData(true, OperatorActorKind.Agent)]
    public async Task OnlyHumanDecisionForExactCandidatePassesGuard(
        bool sameCandidate, OperatorActorKind actorKind)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var goal = kernel.CreateGoal("Protect policy", [task]);
        var candidateSha = new string('a', 40);
        var approvedSha = sameCandidate ? candidateSha : new string('b', 40);
        var intentStore = new SqliteOperatorIntentStore(
            Path.Combine(_root, "operator-intents.db"), Path.Combine(_root, "logs"));
        var decisions = CollaborationItemStore.ForDirectory(_root);
        await intentStore.EnqueueAsync(new OperatorIntentRecord(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, goal.Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(approvedSha, "Owner approved this candidate.")),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: actorKind));
        new OperatorIntentCoordinator(intentStore, decisions: decisions, goalStateVersionResolver: _ => 0)
            .ExecutePending(kernel, goal);
        var verifier = new GoalAcceptanceVerifier(
            DotnetBuildEnvironmentManager.CaptureStorageRoot(), _root);
        Assert.Equal(sameCandidate && actorKind == OperatorActorKind.Human,
            verifier.HasOwnerPolicyApprovalForCandidateTests(goal.Id, candidateSha));

        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), Trusted.Replace("old", "new"));
        var result = GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            _root, ["config/acceptance-manifest.json"], (_, _) => Trusted,
            decisions, goal.Id, candidateSha);

        if (sameCandidate && actorKind == OperatorActorKind.Human)
            Assert.Null(result);
        else
            Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(result).ResultSummary);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
