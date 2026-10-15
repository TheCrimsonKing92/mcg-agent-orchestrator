using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: real intent/portfolio stores and receipt files belong to a unique root per case.
public sealed class EpicOutcomeIntentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "epic-intent-" + Guid.NewGuid().ToString("n"));
    private static EpicOutcomeDefinition Definition(long version = 0) => new(1, version, "Outcome",
        [new("service", "Owner observes service behavior", "file-sha256", "owner-receipt")]);

    [Fact]
    public void Default_coordinator_applies_workspace_definition_and_preserves_replay_identity()
    {
        var workspace = Workspace();
        var epic = new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Epic").GetAwaiter().GetResult();
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var intent = Submit(store, OperatorIntentVerbs.EpicOutcomeDefine,
            new EpicOutcomeDefineOperatorIntentPayload(epic.Id, Definition()));
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(EpicOutcomeState.Unknown, EpicProgressReadModel.LoadEpic(workspace, epic.Id).Outcome.State);
        Assert.Equal(0, EpicProgressReadModel.LoadEpic(workspace, epic.Id).Outcome.BarVersion);
        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        Assert.Single(coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel()));
        Assert.Equal(OperatorIntentStatus.Applied, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
        var handler = new EpicOutcomeIntentHandler(workspace.PortfolioStorePath);
        Assert.True(handler.Apply(intent)); // Recovery after portfolio commit and before inbox completion.
        var history = new EpicPlanStore(workspace.PortfolioStorePath).LoadAsync(epic.Id).GetAwaiter().GetResult().Outcome!;
        Assert.Single(history.Revisions);
        Assert.Equal(1, history.Version);
    }

    [Theory]
    [InlineData("unauthenticated", "tier-below-mutate")]
    [InlineData("steward", "steward-capability-boundary")]
    public void Unauthorized_mutation_is_rejected_without_portfolio_changes(string assurance, string reason)
    {
        var workspace = Workspace();
        var epic = new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Epic").GetAwaiter().GetResult();
        var before = File.ReadAllBytes(workspace.PortfolioStorePath);
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var intent = Submit(store, OperatorIntentVerbs.EpicOutcomeDefine,
            new EpicOutcomeDefineOperatorIntentPayload(epic.Id, Definition()), assurance);
        OperatorIntentCoordinator.CreateDefault(workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel());
        var result = store.GetAsync(intent.Id).GetAwaiter().GetResult()!;
        Assert.Equal(OperatorIntentStatus.Rejected, result.Status);
        Assert.Contains(reason, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));
        Assert.Equal(0, EpicProgressReadModel.LoadEpic(workspace, epic.Id).Outcome.BarVersion);
    }

    [Theory]
    [InlineData("removed", "outcome-missing-receipt")]
    [InlineData("changed", "outcome-receipt-identity-mismatch")]
    [InlineData("locked", "outcome-inaccessible-evidence")]
    [InlineData("authority", "outcome-ambiguous-provenance")]
    [InlineData("version", "outcome-stale-version")]
    public void Evidence_is_revalidated_at_application_without_mutating_history(string change, string reason)
    {
        var workspace = Workspace();
        var epic = new PortfolioStore(workspace.PortfolioStorePath).AddEpicAsync("Epic").GetAwaiter().GetResult();
        var plans = new EpicPlanStore(workspace.PortfolioStorePath);
        var owner = new EpicOutcomeAttribution("owner", OperatorActorKind.Human, "cli", AuthorizationTier.Mutate);
        plans.DefineOutcomeAsync(epic.Id, Definition(), "definition", owner).GetAwaiter().GetResult();
        var file = Path.Combine(root, "receipt.txt");
        File.WriteAllText(file, "Observed behavior");
        var source = new EpicOutcomeSource("file-sha256", file, "owner-receipt", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        var evidence = new EpicOutcomeEvidence(1, 1, "service", 0, true, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), source);
        Assert.NotEmpty(EpicOutcomeReceipts.Read(source)); // The submission-time receipt really exists.
        if (change == "authority") evidence = evidence with { Source = source with { Authority = "another-owner" } };
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var intent = Submit(store, OperatorIntentVerbs.EpicOutcomeRecord, new EpicOutcomeRecordOperatorIntentPayload(epic.Id, evidence));
        if (change == "removed") File.Delete(file);
        if (change == "changed") File.WriteAllText(file, "Different receipt");
        if (change == "version") plans.DefineOutcomeAsync(epic.Id, Definition(1), "revision", owner).GetAwaiter().GetResult();
        using var locked = change == "locked" ? new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var before = JsonSerializer.Serialize(plans.LoadAsync(epic.Id).GetAwaiter().GetResult(), EpicOutcomeContract.JsonOptions);
        OperatorIntentCoordinator.CreateDefault(workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel());
        var result = store.GetAsync(intent.Id).GetAwaiter().GetResult()!;
        Assert.Equal(OperatorIntentStatus.Rejected, result.Status);
        Assert.Contains(reason, result.Outcome);
        Assert.Equal(before, JsonSerializer.Serialize(plans.LoadAsync(epic.Id).GetAwaiter().GetResult(), EpicOutcomeContract.JsonOptions));
    }

    private OrchestratorWorkspace Workspace()
    {
        Directory.CreateDirectory(root);
        return OrchestratorWorkspace.ForDirectory(root);
    }

    private static OperatorIntentRecord Submit<T>(SqliteOperatorIntentStore store, string verb, T payload, string assurance = "local-process")
    {
        var id = Guid.NewGuid().ToString("n");
        return store.EnqueueAsync(new OperatorIntentRecord(id, id, verb, OperatorIntentScopes.Workspace, null,
            JsonSerializer.Serialize(payload, EpicOutcomeContract.JsonOptions), [], "owner", "cli", assurance,
            DateTimeOffset.UtcNow, ActorKind: assurance == "steward" ? OperatorActorKind.Agent : OperatorActorKind.Human)).GetAwaiter().GetResult();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
