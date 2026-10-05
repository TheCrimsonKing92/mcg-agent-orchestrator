using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each fixture owns its temporary workspace and backlog database.
public sealed class GoalIntakeRequestMapperBacklogPrefixTests : IDisposable
{
    private const string ItemId = "a123456789abcdef0123456789abcdef";
    private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
    private readonly OrchestratorWorkspace workspace;

    public GoalIntakeRequestMapperBacklogPrefixTests()
    {
        Directory.CreateDirectory(root);
        workspace = OrchestratorWorkspace.ForDirectory(root);
    }

    [Fact]
    public async Task Map_unique_eight_character_prefix_returns_suggested_objective()
    {
        await SeedItem(ItemId);
        var expected = BacklogIntakePlanner.Build(workspace.BacklogStorePath, ItemId, 2)
            .Items.Single().SuggestedObjective;

        var descriptor = Map(ItemId[..8]);

        Assert.Equal(expected, descriptor.Objective);
    }

    [Fact]
    public async Task Map_prefix_and_full_id_have_equal_fingerprints()
    {
        await SeedItem(ItemId);

        var prefix = Map(ItemId[..8]);
        var fullId = Map(ItemId);

        Assert.Equal(fullId.Fingerprint, prefix.Fingerprint);
    }

    [Fact]
    public async Task Map_missing_prefix_reports_existing_prefix_error()
    {
        await SeedItem(ItemId);

        var error = Assert.Throws<InvalidOperationException>(() => Map("ffffffff"));

        Assert.Contains("No backlog item found with id prefix", error.Message);
    }

    [Fact]
    public async Task Map_ambiguous_prefix_reports_store_ambiguity_error()
    {
        await SeedItem(ItemId);
        await SeedItem("a23456789abcdef0123456789abcdef0");

        var error = Assert.Throws<InvalidOperationException>(() => Map("a"));

        Assert.Contains("Ambiguous id prefix 'a' matches multiple items.", error.Message);
        Assert.DoesNotContain("matched=0", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Map_full_id_preserves_positional_fingerprint(bool uppercase)
    {
        await SeedItem(ItemId);
        var id = uppercase ? ItemId.ToUpperInvariant() : ItemId;
        var positional = GoalIntakeRequestMapper.Map(
            ["backlog-intake", id, "--create-goal", "--request-key", "k1"],
            workspace, AgentCatalog.Default().Agents)!;

        var explicitItem = Map(id);

        Assert.Equal(positional.Fingerprint, explicitItem.Fingerprint);
    }

    private GoalIntakeRequestDescriptor Map(string idPrefix) =>
        GoalIntakeRequestMapper.Map(
            ["backlog-intake", "--backlog-item", idPrefix, "--create-goal", "--request-key", "k1"],
            workspace, AgentCatalog.Default().Agents)!;

    private async Task SeedItem(string id)
    {
        var timestamp = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var item = new BacklogItem(id, "Prefix intake", "Implement the intake fix.",
            BacklogItemStatus.Open, timestamp, timestamp, null);
        Assert.True(await new BacklogStore(workspace.BacklogStorePath).UpsertAsync(item));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
