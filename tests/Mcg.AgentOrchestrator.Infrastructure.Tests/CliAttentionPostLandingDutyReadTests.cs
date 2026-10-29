using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Isolated lifecycle/store fixtures; share the landing fixture's git-runner collection.
[Collection(TestCollections.LandingGitRunner)]
public sealed class CliAttentionPostLandingDutyReadTests
{
    private const string Unrelated = "ffffffffaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Parked = "fffffffeaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("bare", false)]
    [InlineData("list", false)]
    [InlineData("summary", true)]
    [InlineData("summary-reversed", false)]
    [InlineData("show", true)]
    [InlineData("show-positional", false)]
    [InlineData("show-option", false)]
    public async Task JsonRoutes_AppendPopulatedDutiesWithOriginalAuthority(string form, bool archive)
    {
        using var fixture = await CreateLandedFixture(archive);
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        var queries = new CountingQueries(SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath));
        var args = JsonArgs(form, fixture.Goal.Id.Value);

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            if (form.StartsWith("show", StringComparison.Ordinal))
                CliAttentionShowJsonQueryCommand.Execute(args, queries, fixture.Workspace);
            else
                CliAttentionJsonQueryCommand.Execute(args, queries, fixture.Workspace);
        });

        using var document = JsonDocument.Parse(output);
        var envelope = document.RootElement;
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        var payload = envelope.GetProperty("payload");
        string[] legacyFields = form.StartsWith("show", StringComparison.Ordinal)
            ? ["goalId", "waits", "gateEvidence", "items", "ownerQuestionHolds"]
            : form.StartsWith("summary", StringComparison.Ordinal)
                ? ["waits", "items", "providerHolds", "ownerQuestionHolds"]
                : ["waits", "gateEvidence", "items", "providerHolds", "ownerQuestionHolds"];
        Assert.Equal(legacyFields.Concat(["postLandingDuties"]),
            payload.EnumerateObject().Select(property => property.Name));
        var row = Assert.Single(payload.GetProperty("postLandingDuties").EnumerateArray());
        Assert.Equal(fixture.Goal.Id.Value, row.GetProperty("goalId").GetString());
        Assert.Equal(duty.Id, row.GetProperty("obligationId").GetString());
        Assert.Equal(duty.CriterionVersion, row.GetProperty("criterionVersion").GetInt32());
        Assert.Equal("PostLanding", row.GetProperty("phase").GetString());
        Assert.Equal("Pending", row.GetProperty("state").GetString());
        Assert.Equal("Operator", row.GetProperty("owner").GetString());
        Assert.Equal(fixture.Candidate, row.GetProperty("originalArtifact").GetString());
        Assert.Equal(duty.Publication!.PromotedMainRevision,
            row.GetProperty("publication").GetProperty("promotedMainRevision").GetString());
        Assert.Equal(duty.ObservationScope!.Target,
            row.GetProperty("observationScope").GetProperty("target").GetString());
        Assert.Equal(duty.ObservationScope.Condition,
            row.GetProperty("observationScope").GetProperty("condition").GetString());
        Assert.Contains("criterion-evidence-record --goal " + fixture.Goal.Id.Value,
            row.GetProperty("nextAction").GetString());
        Assert.Contains(duty.Id, row.GetProperty("nextAction").GetString());
        if (form.StartsWith("summary", StringComparison.Ordinal))
            Assert.Equal("gateEvidence", Assert.Single(envelope.GetProperty("omittedSections").EnumerateArray()).GetString());
        if (!form.StartsWith("show", StringComparison.Ordinal))
            Assert.Empty(queries.LoadCalls); // Duty projection must not hydrate unrelated goals.
        await AssertStillPending(fixture, duty.Id);
    }

    [Theory]
    [InlineData("attention", false)]
    [InlineData("attention list", false)]
    [InlineData("attention show", true)]
    [InlineData("attention show positional", false)]
    [InlineData("attention show --goal selected", true)]
    public async Task TextRoutes_LoadOnlyDutyOwnerIncludingArchivedCompletedGoal(string command, bool archive)
    {
        using var fixture = await CreateLandedFixture(archive);
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        var queries = new CountingQueries(SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath));
        var args = command.Replace("positional", fixture.Goal.Id.Value, StringComparison.Ordinal)
            .Replace("selected", fixture.Goal.Id.Value, StringComparison.Ordinal).Split(' ');
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var served = false;

        var output = AsyncLocalConsoleRouter.Capture(() => served = CliAttentionQueryCommand.TryExecute(args, queries,
            fixture.Workspace, new InMemoryModelProviderRegistry([]), null,
            ref agents, ref profiles, ref currentGoal));

        Assert.True(served);
        Assert.Equal(new[] { fixture.Goal.Id }, Assert.Single(queries.LoadCalls));
        Assert.Contains("phase=PostLanding state=Pending owner=Operator", output);
        Assert.Contains(duty.Id, output);
        Assert.Contains("artifact=" + fixture.Candidate, output);
        Assert.Contains("target=" + duty.ObservationScope!.Target, output);
        Assert.Contains("criterion-evidence-record --goal " + fixture.Goal.Id.Value, output);
        Assert.DoesNotContain(Unrelated, output);
        Assert.DoesNotContain(Parked, output);
        await AssertStillPending(fixture, duty.Id);
    }

    [Theory]
    [InlineData("attention")]
    [InlineData("attention list")]
    [InlineData("attention show")]
    public async Task TextRoutes_UnsupportedProjection_DiscoversArchivedDuty(string command)
    {
        using var fixture = await CreateLandedFixture(archive: true);
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        var queries = new CountingQueries(
            SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath), supportsProjection: false);
        var args = command.Split(' ');
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var providers = new InMemoryModelProviderRegistry([]);

        var output = AsyncLocalConsoleRouter.Capture(() => Assert.True(
            CliAttentionQueryCommand.TryExecute(args, queries, fixture.Workspace, providers, null,
                ref agents, ref profiles, ref currentGoal)));

        Assert.Equal(2, queries.LoadCalls.Count);
        Assert.Equal(new[] { fixture.Goal.Id, new GoalId(Unrelated), new GoalId(Parked) }.OrderBy(id => id.Value),
            queries.LoadCalls[0].OrderBy(id => id.Value));
        Assert.Equal(new[] { fixture.Goal.Id }, queries.LoadCalls[1]);
        Assert.Contains(duty.Id, output);
        Assert.Contains("phase=PostLanding state=Pending owner=Operator", output);
        Assert.Contains("artifact=" + fixture.Candidate, output);
        await AssertStillPending(fixture, duty.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedJsonShow_UnrelatedGoalRetainsLegacyFieldSet(bool option)
    {
        using var fixture = await CreateLandedFixture(archive: false);
        var queries = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath);
        string[] args = option
            ? ["attention", "show", "--goal", "FFFFFFFF", "--json"]
            : ["attention", "show", "FFFFFFFF", "--json"];

        var output = AsyncLocalConsoleRouter.Capture(() => CliAttentionShowJsonQueryCommand.Execute(args, queries, fixture.Workspace));

        using var document = JsonDocument.Parse(output);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal(Unrelated, payload.GetProperty("goalId").GetString());
        Assert.Equal(new[] { "goalId", "waits", "gateEvidence", "items", "ownerQuestionHolds" },
            payload.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("postLandingDuties", output);
        Assert.DoesNotContain(Assert.Single(fixture.Goal.OutstandingPostLandingDuties).Id, output);
    }

    private static string[] JsonArgs(string form, string goalId) => form switch
    {
        "bare" => ["attention", "--json"],
        "list" => ["attention", "list", "--json"],
        "summary" => ["attention", "list", "--summary", "--json"],
        "summary-reversed" => ["attention", "list", "--json", "--summary"],
        "show" => ["attention", "show", "--json"],
        "show-positional" => ["attention", "show", goalId, "--json"],
        "show-option" => ["attention", "show", "--goal", goalId, "--json"],
        _ => throw new ArgumentOutOfRangeException(nameof(form))
    };

    private static async Task<PostLandingObligationTests.Fixture> CreateLandedFixture(bool archive)
    {
        var fixture = await PostLandingObligationTests.Fixture.Create();
        try
        {
            await fixture.Land();
            _ = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
            fixture.Kernel.CreateGoal(new GoalId(Unrelated), "Unrelated live goal");
            fixture.Kernel.CreateGoal(new GoalId(Parked), "Unrelated parked goal");
            fixture.Kernel.ParkGoal(new GoalId(Parked), "No open attention rows");
            await fixture.Repository.SaveAsync(fixture.Kernel);
            if (archive)
                Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(
                    DateTimeOffset.UtcNow.AddDays(1), compressSnapshots: true));
            await fixture.Reload();
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task AssertStillPending(PostLandingObligationTests.Fixture fixture, string dutyId)
    {
        await fixture.Reload();
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        var duty = Assert.Single(fixture.Goal.OutstandingPostLandingDuties);
        Assert.Equal(dutyId, duty.Id);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
        Assert.Equal(fixture.Candidate, duty.Publication!.CandidateRevision);
    }

    private sealed class CountingQueries(IOrchestratorStateQueries inner, bool supportsProjection = true) : IOrchestratorStateQueries
    {
        internal List<GoalId[]> LoadCalls { get; } = [];
        public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default) =>
            supportsProjection ? inner.ReadAttentionProjectionAsync(cancellationToken) : Task.FromResult<AttentionReadProjection?>(null);
        public Task<AttentionReadProjection?> ReadAttentionProjectionAsync(AttentionProjectionReadOptions options,
            CancellationToken cancellationToken) => supportsProjection
                ? inner.ReadAttentionProjectionAsync(options, cancellationToken) : Task.FromResult<AttentionReadProjection?>(null);
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            inner.ListGoalMetadataAsync(cancellationToken);
        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(CancellationToken cancellationToken = default) =>
            inner.ListOpenHumanInputRequestsAsync(cancellationToken);
        public Task<IReadOnlyList<GoalId>> ListGateTestObligationGoalIdsAsync(IReadOnlyCollection<GoalId>? goalIds = null,
            CancellationToken cancellationToken = default) => inner.ListGateTestObligationGoalIdsAsync(goalIds, cancellationToken);
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            LoadCalls.Add(goalIds.ToArray());
            return inner.LoadGoalsAsync(goalIds, cancellationToken);
        }
    }
}
