using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class OwnerPolicyApprovalRebaseTests : IDisposable
{
    private const string ManifestPath = "config/acceptance-manifest.json";
    private const string Trusted = """{"ownedCollections":["base"]}""";
    private const string Approved = """{"ownedCollections":["base","first","second"]}""";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Fact]
    public async Task ApprovalSurvivesRebaseWithIdenticalProtectedChange()
    {
        var repo = CreateRepo();
        var (goal, decisions) = await Approve(repo, withFingerprint: true);
        var candidateA = Head(repo);
        Assert.Null(Check(repo, decisions, goal.Id));

        RebaseOntoMovedMain(repo);
        Assert.NotEqual(candidateA, Head(repo));
        Assert.Null(Check(repo, decisions, goal.Id));
        Assert.NotNull((await decisions.GetDecisionStateAsync(
            AcceptancePolicyChangeDecision.RequestId(goal.Id.Value, candidateA)))?.Receipt);
    }

    [Xunit.Theory]
    [Xunit.InlineData("""{"ownedCollections":["base","first","second","extra"]}""")]
    [Xunit.InlineData("""{"ownedCollections":["base","first","changed"]}""")]
    public async Task ChangedProtectedFieldNeedsNewDecision(string alteredManifest)
    {
        var repo = CreateRepo();
        var (goal, decisions) = await Approve(repo, withFingerprint: true);
        RebaseOntoMovedMain(repo);
        File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), alteredManifest);
        Run(repo, "add", ManifestPath);
        Run(repo, "commit", "--amend", "--no-edit");

        var failure = Assert.IsType<AcceptanceCheckResult>(Check(repo, decisions, goal.Id));
        Assert.Equal("owner-protected configuration", failure.Name);
        Assert.Equal("operator review required", failure.ResultSummary);
        Assert.Contains("owner decision required for this candidate", failure.OutputTail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task LegacyApprovalRemainsExactCommitAndApprovalIsGoalScoped()
    {
        var repo = CreateRepo();
        var (legacyGoal, decisions) = await Approve(repo, withFingerprint: false);
        var (otherGoal, _) = await Approve(repo, withFingerprint: true, decisions);
        Assert.Null(Check(repo, decisions, legacyGoal.Id));
        RebaseOntoMovedMain(repo);

        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(
            Check(repo, decisions, legacyGoal.Id)).ResultSummary);
        Assert.Null(Check(repo, decisions, otherGoal.Id));
        var unapprovedGoal = new AgentOrchestratorKernel().CreateGoal("Unapproved goal",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        Assert.Equal("operator review required", Assert.IsType<AcceptanceCheckResult>(
            Check(repo, decisions, unapprovedGoal.Id)).ResultSummary);
    }

    private string CreateRepo()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "config"));
        Run(repo, "init", "-b", "main");
        Run(repo, "config", "user.email", "owner@example.test");
        Run(repo, "config", "user.name", "Test Owner");
        File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), Trusted);
        Run(repo, "add", ManifestPath);
        Run(repo, "commit", "-m", "baseline");
        Run(repo, "switch", "-c", "goal");
        File.WriteAllText(Path.Combine(repo, "config", "acceptance-manifest.json"), Approved);
        Run(repo, "add", ManifestPath);
        Run(repo, "commit", "-m", "protected change");
        return repo;
    }

    private async Task<(Goal Goal, ICollaborationItemStore Decisions)> Approve(
        string repo, bool withFingerprint, ICollaborationItemStore? existing = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Protect policy", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var statePath = Path.Combine(_root, goal.Id.Value);
        Directory.CreateDirectory(statePath);
        var intents = new SqliteOperatorIntentStore(Path.Combine(statePath, "intents.db"), Path.Combine(statePath, "logs"));
        var decisions = existing ?? CollaborationItemStore.ForDirectory(Path.Combine(_root, "decisions"));
        await intents.EnqueueAsync(new OperatorIntentRecord(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, goal.Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(Head(repo), "Owner approved protected change.")),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));
        new OperatorIntentCoordinator(intents, decisions: decisions, goalStateVersionResolver: _ => 0,
            policyChangeFingerprintResolver: withFingerprint
                ? (_, sha) => GoalAcceptanceVerifier.ComputeOwnerProtectedChangeFingerprintForCandidate(repo, sha, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default)
                : null).ExecutePending(kernel, goal);
        return (goal, decisions);
    }

    private static AcceptanceCheckResult? Check(string repo, ICollaborationItemStore decisions, GoalId goalId)
    {
        var changed = Run(repo, "diff", "--name-only", "main...HEAD", "--")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return GoalAcceptanceVerifier.TryClassifyManifestTrustWithGitForTests(
            repo, changed, AcceptanceGitTextResolver.Resolve, decisions, goalId, Head(repo));
    }

    private static void RebaseOntoMovedMain(string repo)
    {
        Run(repo, "switch", "main");
        File.WriteAllText(Path.Combine(repo, "unrelated.txt"), "move main");
        Run(repo, "add", "unrelated.txt");
        Run(repo, "commit", "-m", "move main");
        Run(repo, "switch", "goal");
        Run(repo, "rebase", "main");
    }

    private static string Head(string repo) => Run(repo, "rev-parse", "HEAD").Trim();

    private static string Run(string repo, params string[] args)
    {
        var result = GitCli.Run(repo, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output;
    }

    public void Dispose()
    {
        // Git marks object files read-only on Windows; Directory.Delete cannot remove them.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        SharedTestSupport.RemoveTempDirectory(_root);
    }
}
