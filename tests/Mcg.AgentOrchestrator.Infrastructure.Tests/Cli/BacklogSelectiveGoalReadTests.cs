using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: case-owned stores, instance observers and AsyncLocal console routing.
public sealed class BacklogSelectiveGoalReadTests(ITestOutputHelper output) : CliTaskQueryTestSupport
{
    private const string ItemId = "11111111111111111111111111111111";
    private const string OwnerId = "22222222222222222222222222222222";
    private const string DependencyId = "33333333333333333333333333333333";
    private const string CompletedId = "44444444444444444444444444444444";
    private static readonly DateTimeOffset Stamp = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(128, 256)]
    [InlineData(128, 32768)]
    [InlineData(512, 256)]
    [InlineData(512, 32768)]
    public async Task Show_UnrelatedPayloadGrowth_OnlyDeserializesRequiredGoals(int count, int payload)
    {
        using var fixture = await Fixture.Create(count, payload);
        string[] args = ["backlog-show", ItemId, "--json"];
        // Warm each arm; setup and warm-up are outside the measured interval.
        _ = Run(args, fixture.Workspace, baseline: true);
        _ = Run(args, fixture.Workspace, baseline: false);
        var baseline = Run(args, fixture.Workspace, baseline: true);
        var candidate = Run(args, fixture.Workspace, baseline: false);
        Report($"fixed-v1/count={count}/payload={payload}", "eager-7b393558", baseline);
        Report($"fixed-v1/count={count}/payload={payload}", "selective", candidate);
        Assert.Equal(Normalize(baseline.Text), Normalize(candidate.Text));
        Assert.Equal(new[] { OwnerId, DependencyId }.Order(), candidate.Requested.Order());
        Assert.Equal(new[] { OwnerId, DependencyId }.Order(), candidate.Deserialized.Order());
        Assert.Equal(0, candidate.Count("metadata"));
        Assert.Equal(1, candidate.Count("backlog-relationships"));
        Assert.Equal(count + 3, baseline.Count("hot-deserialized"));
        Assert.Equal(count + 3, baseline.Requested.Length);
        Assert.Equal(1, baseline.Count("metadata"));
        Assert.True(baseline.Characters > candidate.Characters);
        // Negative control: the exact former entry setup violates the selected-read invariant.
        Assert.Contains(baseline.Deserialized, id => id != OwnerId && id != DependencyId);
        Assert.DoesNotContain(candidate.Deserialized, id => id != OwnerId && id != DependencyId);
    }

    [Theory]
    [InlineData("backlog-list", false)]
    [InlineData("backlog-list", true)]
    [InlineData("backlog-show", false)]
    [InlineData("backlog-show", true)]
    public async Task SelectedReads_ArchivedLinkedAndDependencyGoals_PreserveResults(string verb, bool json)
    {
        using var fixture = await Fixture.Create(32, 4096);
        await fixture.MakeTerminalAndArchive(OwnerId, DependencyId);
        var args = verb == "backlog-list"
            ? new[] { verb, "--text", "selected", "--limit", "1" }
            : new[] { verb, ItemId };
        if (json) args = [.. args, "--json"];
        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(Normalize(baseline.Text), Normalize(candidate.Text));
        Assert.Equal(new[] { OwnerId, DependencyId }.Order(), candidate.Requested.Order());
        Assert.Equal(new[] { OwnerId, DependencyId }.Order(), candidate.Archived.Order());
        Assert.Equal(2, candidate.Count("hot-deserialized"));
        if (verb == "backlog-list" && json)
            Assert.False(JsonNode.Parse(candidate.Text)!["payload"]!["items"]![0]!["eligible"]!.GetValue<bool>());
        if (verb == "backlog-show") Assert.Contains(json ? "authoritative" : "authority=authoritative", candidate.Text);
    }

    [Theory]
    [InlineData("backlog-similar", false)]
    [InlineData("backlog-similar", true)]
    public async Task Similarity_HotAndArchivedCorpus_PreservesRankingWithoutSnapshots(string verb, bool archive)
    {
        using var fixture = await Fixture.Create(64, 32768);
        // A smaller ID inserted later distinguishes primary-key order from insertion order.
        const string twinId = "00000000000000000000000000000001";
        var writer = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var original = (await writer.LoadGoalsAsync([new GoalId(CompletedId)])).ExportSnapshot().Goals.Single();
        await writer.SaveGoalSnapshotsAsync([original with
        {
            Id = twinId,
            Timeline = original.Timeline.Select(evt => evt with { GoalId = twinId }).ToArray()
        }]);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        string[] args = [verb, "quartz", "--excerpt", "--limit", "100"];
        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(baseline.Text, candidate.Text);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", candidate.Text);
        Assert.Contains($"kind=goal id={twinId[..8]}", candidate.Text);
        Assert.True(candidate.Text.IndexOf($"kind=goal id={twinId[..8]}", StringComparison.Ordinal) <
            candidate.Text.IndexOf($"kind=goal id={CompletedId[..8]}", StringComparison.Ordinal));
        Assert.Empty(candidate.Requested);
        Assert.Empty(candidate.Deserialized);
        Assert.Empty(candidate.Archived);
        Assert.Equal(1, candidate.Count("similarity-metadata"));
        Assert.Equal(2, candidate.Count("similarity-projected"));
        Report("fixed-v1/similarity", archive ? "archived-projection" : "hot-projection", candidate);
    }

    [Theory]
    [InlineData("backlog-list", false)]
    [InlineData("backlog-list", true)]
    [InlineData("backlog-show", false)]
    [InlineData("backlog-show", true)]
    public async Task Ownership_AmbiguousLegacyAndMissingReferences_PreserveUnknowns(string verb, bool json)
    {
        using var fixture = await Fixture.Create(16, 1024);
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var kernel = await repository.LoadAsync();
        kernel.SetGoalSourceBacklogItemId(new GoalId(DependencyId), ItemId);
        await repository.SaveAsync(kernel);
        // Missing references must remain explicit even though no snapshot is returned.
        const string missing = "99999999999999999999999999999999";
        await fixture.Store.AddDependencyAsync(ItemId, new(missing, BacklogDependencyTargetKind.Goal), goalExists: _ => true);
        string[] args = verb == "backlog-list" ? [verb, "--text", "selected"] : [verb, ItemId];
        if (json) args = [.. args, "--json"];
        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(Normalize(baseline.Text), Normalize(candidate.Text));
        Assert.Contains(json ? "\"ownerAmbiguous\":true" : "legacy-owner-ambiguous", candidate.Text);
        Assert.Equal(new[] { OwnerId, DependencyId }.Order(), candidate.Deserialized.Order());
        if (verb == "backlog-show")
        {
            Assert.Contains(missing, candidate.Text);
            Assert.Contains("missing", candidate.Text);
            Assert.Contains(missing, candidate.Requested);
        }
    }

    [Fact]
    public async Task List_RenderedStatusBeforeLimit_LoadsOwnersBeforeSelecting()
    {
        using var fixture = await Fixture.Create(16, 256);
        // Put an unclaimed item before the claimed item in store order. Limiting before
        // evaluating rendered status would incorrectly discard the claimed result.
        await fixture.Store.AddAsync("Unclaimed newest", "quartz");
        fixture.ExecuteBacklog("UPDATE backlog SET created_at = '2026-08-01T00:00:00Z' WHERE id <> '" + ItemId + "'");
        Assert.NotEqual(ItemId, (await fixture.Store.ListAsync(includeAll: true))[0].Id);
        string[] args = ["backlog-list", "--status", "claimed", "--limit", "1", "--json"];
        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(Normalize(baseline.Text), Normalize(candidate.Text));
        using var parsed = JsonDocument.Parse(candidate.Text);
        Assert.Equal(ItemId, Assert.Single(parsed.RootElement.GetProperty("payload")
            .GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain(candidate.Deserialized, id => id != OwnerId && id != DependencyId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedClaim_CompetingLinkAndMissingOwner_PreservesAuthority(bool missingOwner)
    {
        using var fixture = await Fixture.Create(16, 256);
        var writer = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var claims = new SourceBacklogClaimStore(fixture.Workspace.SqliteStatePath);
        await writer.TransactAsync((kernel, _) =>
        {
            var claim = claims.ResolveOrMaterializeClaim(kernel, ItemId);
            Assert.Equal(OwnerId, claim.OwnerGoalId);
            kernel.SetGoalSourceBacklogItemId(new GoalId(DependencyId), ItemId);
            return Task.FromResult((ShouldSave: true, Result: claim));
        });
        fixture.ExecuteState($"""
            UPDATE source_backlog_claims SET version = 2 WHERE backlog_item_id = '{ItemId}';
            INSERT INTO goal_replacement_lineage
                (predecessor_goal_id, successor_goal_id, backlog_item_id, request_id, replaced_at)
            VALUES ('{DependencyId}', '{OwnerId}', '{ItemId}', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                '2026-09-01T00:00:00.0000000+00:00');
            """);
        if (missingOwner) fixture.ExecuteState("DELETE FROM goals WHERE id = '" + OwnerId + "'");
        foreach (var args in new[] { new[] { "backlog-show", ItemId, "--json" }, new[] { "backlog-list", "--json" } })
        {
            var baseline = Run(args, fixture.Workspace, true);
            var candidate = Run(args, fixture.Workspace, false);
            Assert.Equal(Normalize(baseline.Text), Normalize(candidate.Text));
            var node = JsonNode.Parse(candidate.Text)!["payload"]!;
            if (args[0] == "backlog-show")
            {
                Assert.Equal(OwnerId, node["owner"]!.GetValue<string>());
                Assert.Equal(2, node["claimVersion"]!.GetValue<long>());
                var lineage = Assert.Single(node["lineage"]!.AsArray())!;
                Assert.Equal(DependencyId, lineage["predecessorGoalId"]!.GetValue<string>());
                Assert.Equal(OwnerId, lineage["successorGoalId"]!.GetValue<string>());
            }
            else
                Assert.Equal(missingOwner ? "open" : "claimed", node["items"]![0]!["status"]!.GetValue<string>());
            Assert.DoesNotContain(candidate.Deserialized, id => id != OwnerId && id != DependencyId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyChain_UnreturnedPrerequisite_UsesExistingDirectOwnerRule(bool claimed)
    {
        using var fixture = await Fixture.Create(16, 256);
        var middle = await fixture.Store.AddAsync("Middle prerequisite", "quartz");
        var tail = await fixture.Store.AddAsync("Tail prerequisite", "quartz");
        await fixture.Store.AddDependencyAsync(middle.Id, new(tail.Id, BacklogDependencyTargetKind.Backlog));
        await fixture.Store.ClearDependenciesAsync(ItemId);
        await fixture.Store.AddDependencyAsync(ItemId, new(middle.Id, BacklogDependencyTargetKind.Backlog));
        if (claimed)
        {
            var writer = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
            var kernel = await writer.LoadAsync();
            kernel.SetGoalSourceBacklogItemId(new GoalId(DependencyId), middle.Id);
            await writer.SaveAsync(kernel);
        }
        string[] args = ["backlog-list", "--text", "selected", "--limit", "1", "--json"];
        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(Normalize(baseline.Text), Normalize(candidate.Text));
        var item = JsonNode.Parse(candidate.Text)!["payload"]!["items"]![0]!;
        Assert.False(item["eligible"]!.GetValue<bool>());
        Assert.Equal(claimed ? new[] { OwnerId, DependencyId } : [OwnerId], candidate.Deserialized);
        // The old rule inspects the direct prerequisite owner, not recursive readiness.
        Assert.Equal(2, candidate.Count("backlog-relationships"));
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--text", "no-match-in-fixture")]
    public async Task List_EmptySelection_DoesNotReadGoalMetadataOrSnapshots(string flag, string value)
    {
        using var fixture = await Fixture.Create(32, 32768);
        string[] args = ["backlog-list", flag, value, "--json"];
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(Normalize(Run(args, fixture.Workspace, true).Text), Normalize(candidate.Text));
        Assert.Empty(candidate.Events);
        Assert.Empty(JsonNode.Parse(candidate.Text)!["payload"]!["items"]!.AsArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dependency_LandedGoalOutsideResults_RemainsEligible(bool archive)
    {
        using var fixture = await Fixture.Create(16, 1024);
        var writer = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        var kernel = await writer.LoadGoalsAsync([new GoalId(DependencyId)]);
        await writer.SaveGoalSnapshotsAsync([kernel.ExportGoalSnapshot(new GoalId(DependencyId)) with { Status = GoalStatus.Completed }]);
        if (archive) await fixture.MakeTerminalAndArchive(DependencyId);
        kernel = await writer.LoadGoalsAsync([new GoalId(DependencyId)]);
        GoalOperationJournal.Completed(fixture.Workspace.ExecutionDirectory, kernel.Goals.Single(), "conductor:land");
        string[] args = ["backlog-list", "--text", "selected", "--limit", "1", "--json"];
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(Normalize(Run(args, fixture.Workspace, true).Text), Normalize(candidate.Text));
        Assert.True(JsonNode.Parse(candidate.Text)!["payload"]!["items"]![0]!["eligible"]!.GetValue<bool>());
        Assert.Equal(new[] { OwnerId, DependencyId }, candidate.Deserialized);
        Assert.Equal(archive ? [DependencyId] : Array.Empty<string>(), candidate.Archived);
    }

    [Theory]
    [InlineData("trailing")]
    [InlineData("truncated")]
    [InlineData("utf8")]
    public async Task Similarity_InvalidArchiveEncoding_FailsLikeFullHydration(string corruption)
    {
        using var fixture = await Fixture.Create(4, 256);
        await fixture.MakeTerminalAndArchive(CompletedId);
        var valid = GoalSnapshotArchiveCodec.Encode("{}");
        byte[] invalid;
        if (corruption == "utf8")
        {
            using var bytes = new MemoryStream();
            bytes.Write("MGB1"u8);
            using (var compressor = new System.IO.Compression.BrotliStream(bytes,
                System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) compressor.Write([0xff]);
            invalid = bytes.ToArray();
        }
        else invalid = corruption == "trailing" ? [.. valid, 0xff] : valid[..^1];
        fixture.ReplaceArchive(CompletedId, invalid);
        var expected = Assert.Throws<InvalidDataException>(() => Run(["backlog-similar", "quartz"], fixture.Workspace, true));
        var actual = Assert.Throws<InvalidDataException>(() => Run(["backlog-similar", "quartz"], fixture.Workspace, false));
        Assert.Equal(expected.Message, actual.Message);
    }

    [Theory]
    [InlineData("\"invalid\"", "$.Tasks")]
    [InlineData("{}", "$.Tasks")]
    [InlineData("42", "$.Tasks")]
    [InlineData("[\"invalid\"]", "$.Tasks[0]")]
    [InlineData("[{\"Description\":42}]", "$.Tasks[0].Description")]
    [InlineData("[{\"RequiredRole\":\"invalid-role\"}]", "$.Tasks[0].RequiredRole")]
    [InlineData("[{\"LastExecution\":{\"Output\":42}}]", "$.Tasks[0].LastExecution.Output")]
    [InlineData("[{\"CriterionRetryFeedback\":[42]}]", "$.Tasks[0].CriterionRetryFeedback[0]")]
    public async Task Similarity_InvalidHotTasks_QuarantinesLikeFullHydration(string tasks, string path)
    {
        using var fixture = await Fixture.Create(4, 256);
        string[] args = ["backlog-similar", "quartz", "--excerpt", "--limit", "100"];
        const string validId = "55555555555555555555555555555555";
        await fixture.AddSimilarityTwin(validId);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", Run(args, fixture.Workspace, false).Text);
        fixture.ReplaceTasks(CompletedId, tasks);

        Receipt baseline = null!, candidate = null!;
        var expectedError = AsyncLocalConsoleRouter.CaptureError(() => baseline = Run(args, fixture.Workspace, true));
        var actualError = AsyncLocalConsoleRouter.CaptureError(() => candidate = Run(args, fixture.Workspace, false));
        Assert.Contains($"QUARANTINED goal {CompletedId[..8]}: JsonException:", expectedError);
        Assert.Contains(path, expectedError);
        Assert.Equal(expectedError, actualError);
        Assert.Equal(baseline.Text, candidate.Text);
        Assert.DoesNotContain($"kind=goal id={CompletedId[..8]}", candidate.Text);
        Assert.Contains($"kind=goal id={validId[..8]}", candidate.Text);
        Assert.Equal(1, candidate.Count("similarity-projected"));
        Assert.Empty(candidate.Requested);
        Assert.Empty(candidate.Deserialized);
    }

    [Theory]
    [InlineData("\"invalid\"", "$.Tasks")]
    [InlineData("{}", "$.Tasks")]
    [InlineData("42", "$.Tasks")]
    [InlineData("[\"invalid\"]", "$.Tasks[0]")]
    [InlineData("[{\"Description\":42}]", "$.Tasks[0].Description")]
    [InlineData("[{\"RequiredRole\":\"invalid-role\"}]", "$.Tasks[0].RequiredRole")]
    [InlineData("[{\"LastExecution\":{\"Output\":42}}]", "$.Tasks[0].LastExecution.Output")]
    [InlineData("[{\"CriterionRetryFeedback\":[42]}]", "$.Tasks[0].CriterionRetryFeedback[0]")]
    public async Task Similarity_InvalidArchivedTasks_ThrowsLikeFullHydration(string tasks, string path)
    {
        using var fixture = await Fixture.Create(4, 256);
        await fixture.MakeTerminalAndArchive(CompletedId);
        string[] args = ["backlog-similar", "quartz", "--excerpt", "--limit", "100"];
        Assert.Contains($"kind=goal id={CompletedId[..8]}", Run(args, fixture.Workspace, false).Text);
        fixture.ReplaceTasks(CompletedId, tasks, archive: true);

        JsonException expected = null!, actual = null!;
        var expectedError = AsyncLocalConsoleRouter.CaptureError(() => expected = Assert.Throws<JsonException>(
            () => Run(args, fixture.Workspace, true)));
        var actualError = AsyncLocalConsoleRouter.CaptureError(() => actual = Assert.Throws<JsonException>(
            () => Run(args, fixture.Workspace, false)));
        Assert.Equal(path, expected.Path);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.LineNumber, actual.LineNumber);
        Assert.Equal(expected.BytePositionInLine, actual.BytePositionInLine);
        Assert.Empty(expectedError);
        Assert.Empty(actualError); // Archive corruption throws; it must not become a quarantine/skip.
    }

    public static IEnumerable<object[]> InvalidSimilarityMembers()
    {
        string[][] cases =
        [
            ["SourceBacklogItemId", "42", "$.SourceBacklogItemId"],
            ["DependsOn", "[42]", "$.DependsOn[0]"],
            ["AutomaticAcceptanceRetryCount", "\"invalid\"", "$.AutomaticAcceptanceRetryCount"],
            ["RefinedSpec", "{\"BehavioralContract\":42}", "$.RefinedSpec.BehavioralContract"],
            ["Timeline", "{}", "$.Timeline"],
            ["Timeline", "42", "$.Timeline"],
            ["Timeline", "[42]", "$.Timeline[0]"],
            ["Timeline", "[{\"Message\":42}]", "$.Timeline[0].Message"],
            ["Timeline", "[{\"GoalId\":42}]", "$.Timeline[0].GoalId"],
            ["Timeline", "[{\"TaskId\":42}]", "$.Timeline[0].TaskId"],
            ["Timeline", "[{\"Kind\":\"invalid-kind\"}]", "$.Timeline[0].Kind"],
            ["Timeline", "[{\"RequeueSkipped\":42}]", "$.Timeline[0].RequeueSkipped"],
            ["Timeline", "[{\"OccurredAt\":\"invalid-time\"}]", "$.Timeline[0].OccurredAt"],
            ["Timeline", "[{\"OccurredAt\":42}]", "$.Timeline[0].OccurredAt"]
        ];
        foreach (var archive in new[] { false, true })
        foreach (var entry in cases)
            yield return [archive, entry[0], entry[1], entry[2]];
    }

    [Theory]
    [MemberData(nameof(InvalidSimilarityMembers))]
    public async Task Similarity_InvalidMembers_PreservesHydrationDiagnostics(
        bool archive, string member, string value, string path)
    {
        using var fixture = await Fixture.Create(4, 256);
        const string validId = "55555555555555555555555555555555";
        await fixture.AddSimilarityTwin(validId);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        string[] args = ["backlog-similar", "quartz", "--excerpt", "--limit", "100"];
        var before = Run(args, fixture.Workspace, false);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", before.Text);
        Assert.Equal(2, before.Count("similarity-projected"));
        fixture.ReplaceSnapshotMember(CompletedId, member, value, archive);

        if (archive)
        {
            JsonException expected = null!, actual = null!;
            var expectedError = AsyncLocalConsoleRouter.CaptureError(() => expected = Assert.Throws<JsonException>(
                () => Run(args, fixture.Workspace, true)));
            var actualError = AsyncLocalConsoleRouter.CaptureError(() => actual = Assert.Throws<JsonException>(
                () => Run(args, fixture.Workspace, false)));
            Assert.Equal(path, expected.Path);
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Message, actual.Message);
            Assert.Equal(expected.Path, actual.Path);
            Assert.Equal(expected.LineNumber, actual.LineNumber);
            Assert.Equal(expected.BytePositionInLine, actual.BytePositionInLine);
            Assert.Empty(expectedError);
            Assert.Empty(actualError);
        }
        else
        {
            Receipt baseline = null!, candidate = null!;
            var expectedError = AsyncLocalConsoleRouter.CaptureError(() => baseline = Run(args, fixture.Workspace, true));
            var actualError = AsyncLocalConsoleRouter.CaptureError(() => candidate = Run(args, fixture.Workspace, false));
            Assert.Contains($"QUARANTINED goal {CompletedId[..8]}: JsonException:", expectedError);
            Assert.Contains(path, expectedError);
            Assert.Equal(expectedError, actualError);
            Assert.Equal(baseline.Text, candidate.Text);
            Assert.DoesNotContain($"kind=goal id={CompletedId[..8]}", candidate.Text);
            Assert.Contains($"kind=goal id={validId[..8]}", candidate.Text);
            Assert.Equal(1, candidate.Count("similarity-projected"));
            Assert.Empty(candidate.Requested);
            Assert.Empty(candidate.Deserialized);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Similarity_EmptyTasks_PreservesReconstructionFailure(bool archive)
    {
        using var fixture = await Fixture.Create(4, 256);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        string[] args = ["backlog-similar", "quartz"];
        Assert.Contains($"kind=goal id={CompletedId[..8]}", Run(args, fixture.Workspace, false).Text);
        fixture.ReplaceTasks(CompletedId, "[]", archive);

        ArgumentException expected = null!, actual = null!;
        var expectedError = AsyncLocalConsoleRouter.CaptureError(() => expected = Assert.Throws<ArgumentException>(
            () => Run(args, fixture.Workspace, true)));
        var actualError = AsyncLocalConsoleRouter.CaptureError(() => actual = Assert.Throws<ArgumentException>(
            () => Run(args, fixture.Workspace, false)));
        Assert.Equal("tasks", expected.ParamName);
        Assert.Contains("A goal must have at least one task.", expected.Message);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.ParamName, actual.ParamName);
        Assert.Empty(expectedError);
        Assert.Empty(actualError); // Domain failures must escape the hot quarantine boundary too.
    }

    public static IEnumerable<object[]> InvalidSimilarityDomainMembers()
    {
        (string Member, string Value, Type Exception)[] cases =
        [
            ("Tasks", "null", typeof(ArgumentNullException)),
            ("Tasks", "[null]", typeof(NullReferenceException)),
            ("Timeline", "null", typeof(ArgumentNullException)),
            ("Timeline", "[null]", typeof(NullReferenceException)),
            ("Objective", "\"   \"", typeof(ArgumentException)),
            ("Objective", "\"\"", typeof(ArgumentException)),
            ("ParkedLifecycleStatus", "\"Active\"", typeof(ArgumentException)),
            ("SourceBacklogCoverage", "\"Full\"", typeof(InvalidOperationException)),
            ("SourceBacklogItemId", "\"   \"", typeof(ArgumentException)),
            ("SourceBacklogItemId", "\"\"", typeof(ArgumentException)),
            ("Tasks", "[{\"Id\":\"66666666666666666666666666666666\",\"Description\":\"   \"}]", typeof(ArgumentException)),
            ("Tasks", "[{\"Id\":\"66666666666666666666666666666666\",\"Description\":\"\"}]", typeof(ArgumentException)),
            ("Tasks", """[{"Id":"66666666666666666666666666666666","Description":"valid","DispatchHistory":[null]}]""",
                typeof(NullReferenceException)),
            ("Tasks", """[{"Id":"66666666666666666666666666666666","Description":"valid","RetryAdmissionHistory":[null]}]""",
                typeof(ArgumentNullException)),
            ("Tasks", """[{"Id":"66666666666666666666666666666666","Description":"valid","VerificationHistory":[null]}]""",
                typeof(NullReferenceException)),
            ("EffectiveAcceptanceCriteriaCorrections", """[{"SupersededCriterion":"old","Correction":"","Actor":"operator"}]""",
                typeof(ArgumentException)),
            ("EffectiveAcceptanceCriteriaCorrections", """[{"SupersededCriterion":"old","Correction":"   ","Actor":"operator"}]""",
                typeof(ArgumentException)),
            ("LatestAcceptanceFailure", """{"FailedChecks":null}""", typeof(ArgumentNullException)),
            ("LatestAcceptanceFailure", """{"FailedChecks":[null]}""", typeof(NullReferenceException)),
            ("GateTestObligations", "[null]", typeof(NullReferenceException)),
            ("RefinedSpecVersions", """[{"Version":1,"Spec":null,"BriefVersion":1}]""", typeof(NullReferenceException)),
            ("RefinedSpecVersions", """
                [{"Version":1,"Spec":{"BehavioralContract":"quartz","AcceptanceCriteria":["quartz"],
                "Decisions":null,"OpenQuestions":[]},"BriefVersion":1}]
                """, typeof(ArgumentNullException)),
            ("RefinedSpec", """{"BehavioralContract":"quartz","AcceptanceCriteria":["quartz"],"Decisions":null,"OpenQuestions":[]}""",
                typeof(ArgumentNullException)),
            ("RefinedSpec", """{"BehavioralContract":"quartz","AcceptanceCriteria":["quartz"],"Decisions":[],"OpenQuestions":null}""",
                typeof(ArgumentNullException)),
            ("BriefVersions", "[{\"Version\":2,\"Text\":\"quartz\",\"RecordedAt\":\"2026-09-01T00:00:00Z\"}]",
                typeof(InvalidOperationException)),
            ("RefinedSpecVersions", """
                [{"Version":2,"Spec":{"BehavioralContract":"quartz","AcceptanceCriteria":["quartz"],
                "VerificationClass":"TestVerifiable","Decisions":[],"OpenQuestions":[]},
                "RecordedAt":"2026-09-01T00:00:00Z","BriefVersion":1}]
                """, typeof(InvalidOperationException))
        ];
        foreach (var archive in new[] { false, true })
        foreach (var entry in cases)
            yield return [archive, entry.Member, entry.Value, entry.Exception];
    }

    [Theory]
    [MemberData(nameof(InvalidSimilarityDomainMembers))]
    public async Task Similarity_InvalidDomainState_PreservesHydrationFailure(
        bool archive, string member, string value, Type exceptionType)
    {
        using var fixture = await Fixture.Create(4, 256);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        if (member is "BriefVersions" or "RefinedSpecVersions" && value.Contains("\"Version\":2", StringComparison.Ordinal))
            fixture.ReplaceSnapshotMember(CompletedId, member,
                value.Replace("\"Version\":2", "\"Version\":1", StringComparison.Ordinal), archive);
        string[] args = ["backlog-similar", "quartz"];
        var before = Run(args, fixture.Workspace, false);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", before.Text);
        Assert.Equal(Run(args, fixture.Workspace, true).Text, before.Text);
        Assert.Equal(1, before.Count("similarity-projected"));
        Assert.Empty(before.Requested);
        Assert.Empty(before.Deserialized);
        fixture.ReplaceSnapshotMember(CompletedId, member, value, archive);

        Exception? expected = null, actual = null;
        var expectedError = AsyncLocalConsoleRouter.CaptureError(() =>
            expected = Record.Exception(() => Run(args, fixture.Workspace, true)));
        var actualError = AsyncLocalConsoleRouter.CaptureError(() =>
            actual = Record.Exception(() => Run(args, fixture.Workspace, false)));
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(exceptionType, expected.GetType());
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
        if (expected is ArgumentException argument)
            Assert.Equal(argument.ParamName, Assert.IsAssignableFrom<ArgumentException>(actual).ParamName);
        Assert.Empty(expectedError);
        Assert.Empty(actualError); // Reconstruction failures must escape hot quarantine too.
    }

    public static IEnumerable<object[]> NestedSimilarityDomainMembers()
    {
        var cases = new List<(string Member, string Valid, string Invalid, Type Exception)>();
        const string correction = """
            [{"SupersededCriterion":"old","Correction":"new","Actor":"operator",
              "Dispositions":[{"Criterion":"quartz","Disposition":"retained"}]}]
            """;
        foreach (var disposition in new[]
        {
            "null", """{"Criterion":null,"Disposition":"retained"}""",
            """{"Criterion":"","Disposition":"retained"}""",
            """{"Criterion":"   ","Disposition":"retained"}""",
            """{"Criterion":"quartz","Disposition":null}""",
            """{"Criterion":"quartz","Disposition":""}""",
            """{"Criterion":"quartz","Disposition":"   "}"""
        })
        {
            var invalid = JsonNode.Parse(correction)!;
            invalid[0]!["Dispositions"] = JsonNode.Parse("[" + disposition + "]");
            cases.Add(("EffectiveAcceptanceCriteriaCorrections", correction, invalid.ToJsonString(),
                disposition == "null" ? typeof(NullReferenceException) : typeof(ArgumentException)));
        }

        const string failure = """
            {"FailedChecks":["quartz"],"AttributionPending":{"CandidateSha":"candidate","MainSha":"main",
             "AttemptId":"attempt","Tests":[],"OtherEvidenceKind":"none"}}
            """;
        foreach (var checks in new[] { "[]", "[\"   \"]" })
        {
            var invalid = JsonNode.Parse(failure)!;
            invalid["FailedChecks"] = JsonNode.Parse(checks);
            cases.Add(("LatestAcceptanceFailure", failure, invalid.ToJsonString(), typeof(NullReferenceException)));
        }

        var obligation = new GateTestObligation("quartz", 1, ["BacklogSelectiveGoalReadTests"], "planner", "evidence", Stamp,
            new PlannerEvidenceIdentity(CompletedId, "planner", 1, 0, "quartz"),
            new PlannerEvidencePayload(0, "quartz", "post-implementation", "acceptance-gate", "needed", "reason",
                null, null, ["BacklogSelectiveGoalReadTests"]));
        // Separate selector arrays prove semantic equality, rather than record/reference equality.
        var matching = obligation with { PlannerEvidence = obligation.PlannerEvidence! with
            { Selectors = ["BacklogSelectiveGoalReadTests"] } };
        cases.Add(("GateTestObligations", JsonSerializer.Serialize(new[] { obligation, matching }),
            JsonSerializer.Serialize(new[] { obligation, matching with
                { PlannerEvidence = matching.PlannerEvidence! with { Needed = "conflicting" } } }),
            typeof(InvalidOperationException)));

        const string spec = """
            {"BehavioralContract":"quartz","AcceptanceCriteria":["quartz"],"Decisions":[],"OpenQuestions":[],
             "OperatorOwnedAcceptanceCriteria":[],"AcceptanceGateOwnedAcceptanceCriteria":[]}
            """;
        foreach (var versioned in new[] { false, true })
        foreach (var entry in new[]
        {
            (Member: "AcceptanceCriteria", Value: "null", Exception: typeof(NullReferenceException)),
            (Member: "AcceptanceCriteria", Value: "[null]", Exception: typeof(ArgumentException)),
            (Member: "AcceptanceCriteria", Value: "[\"\"]", Exception: typeof(ArgumentException)),
            (Member: "AcceptanceCriteria", Value: "[\"   \"]", Exception: typeof(ArgumentException)),
            (Member: "OperatorOwnedAcceptanceCriteria", Value: "[null]", Exception: typeof(NullReferenceException)),
            (Member: "AcceptanceGateOwnedAcceptanceCriteria", Value: "[null]", Exception: typeof(NullReferenceException))
        })
        {
            var invalid = JsonNode.Parse(spec)!;
            invalid[entry.Member] = JsonNode.Parse(entry.Value);
            string Wrap(string contents) => versioned
                ? "[{\"Version\":1,\"Spec\":" + contents + ",\"BriefVersion\":1}]" : contents;
            cases.Add((versioned ? "RefinedSpecVersions" : "RefinedSpec", Wrap(spec), Wrap(invalid.ToJsonString()), entry.Exception));
        }

        const string task = """[{"Id":"66666666666666666666666666666666","Description":"valid","CriterionRetryFeedback":["retry quartz"]}]""";
        cases.Add(("Tasks", task, task.Replace("\"retry quartz\"", "null", StringComparison.Ordinal), typeof(NullReferenceException)));

        var verification = JsonSerializer.SerializeToNode(new TaskVerificationSnapshot("verify", "workspace", 0, "passed", "", Stamp,
            MergedReviewFindings: [new ReviewFinding("quartz", ReviewFindingState.Open, new ReviewFindingLocation("file", "region"), "finding")],
            FindingEvidenceReceipts: [new FindingEvidenceReceipt("receipt", "candidate",
                new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", "BacklogSelectiveGoalReadTests")]),
                true, true, "passed")],
            FindingEvidenceRequestDispositions: [new FindingEvidenceRequestDisposition("quartz", "request", "executed")]))!;
        string WithHistory(JsonNode record)
        {
            var node = JsonNode.Parse(task)!;
            node[0]!["VerificationHistory"] = new JsonArray(record.DeepClone(), record.DeepClone());
            return node.ToJsonString();
        }
        foreach (var member in new[] { "MergedReviewFindings", "FindingEvidenceReceipts", "FindingEvidenceRequestDispositions" })
        {
            var invalid = verification.DeepClone();
            invalid[member] = new JsonArray((JsonNode?)null);
            cases.Add(("Tasks", WithHistory(verification), WithHistory(invalid), typeof(NullReferenceException)));
        }

        foreach (var archive in new[] { false, true })
        foreach (var entry in cases)
            yield return [archive, entry.Member, entry.Valid, entry.Invalid, entry.Exception];
    }

    [Theory]
    [MemberData(nameof(NestedSimilarityDomainMembers))]
    public async Task Similarity_NestedDomainState_PreservesProjectionAndHydrationFailure(
        bool archive, string member, string valid, string invalid, Type exceptionType)
    {
        using var fixture = await Fixture.Create(4, 256);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        // Spec restoration must create the criterion obligation, rather than skip an existing one.
        if (member is "RefinedSpec" or "RefinedSpecVersions")
            fixture.ReplaceSnapshotMember(CompletedId, "CriterionEvidenceObligations", "[]", archive);
        fixture.ReplaceSnapshotMember(CompletedId, member, valid, archive);
        string[] args = ["backlog-similar", "quartz"];
        var before = Run(args, fixture.Workspace, false);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", before.Text);
        Assert.Equal(Run(args, fixture.Workspace, true).Text, before.Text);
        Assert.Equal(1, before.Count("similarity-projected"));
        Assert.Empty(before.Requested);
        Assert.Empty(before.Deserialized);
        Assert.Empty(before.Archived);

        fixture.ReplaceSnapshotMember(CompletedId, member, invalid, archive);
        AssertSimilarityDomainFailure(fixture, args, exceptionType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Similarity_BriefObjective_PreservesHydrationFailure(bool archive)
    {
        using var fixture = await Fixture.Create(4, 256);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        // Deliberately unordered: authority is the highest version, not array order.
        const string versions = """
            [{"Version":2,"Text":"Historical quartz match\nquartz body","SupersededByVersion":null},
             {"Version":1,"Text":"original quartz","SupersededByVersion":2}]
            """;
        fixture.ReplaceSnapshotMember(CompletedId, "BriefVersions", versions, archive);
        string[] args = ["backlog-similar", "quartz"];
        var before = Run(args, fixture.Workspace, false);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", before.Text);
        Assert.Equal(Run(args, fixture.Workspace, true).Text, before.Text);
        Assert.Equal(1, before.Count("similarity-projected"));
        Assert.Empty(before.Requested);
        Assert.Empty(before.Deserialized);

        fixture.ReplaceSnapshotMember(CompletedId, "BriefVersions",
            versions.Replace("quartz body", "divergent body", StringComparison.Ordinal), archive);
        AssertSimilarityDomainFailure(fixture, args, typeof(InvalidOperationException));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Similarity_InvalidMembers_PreservesFailureOrder(bool archive)
    {
        using var fixture = await Fixture.Create(4, 256);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        string[] args = ["backlog-similar", "quartz"];
        Assert.Contains($"kind=goal id={CompletedId[..8]}", Run(args, fixture.Workspace, false).Text);
        fixture.ReplaceSnapshotMember(CompletedId, "Objective", "\"\"", archive);
        fixture.ReplaceSnapshotMember(CompletedId, "Tasks", "[]", archive);
        AssertSimilarityDomainFailure(fixture, args, typeof(ArgumentException), "objective");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Similarity_MetadataActiveStatus_PreservesHydrationFailure(bool archive)
    {
        using var fixture = await Fixture.Create(4, 256);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        fixture.ReplaceSnapshotMember(CompletedId, "IsMetadataOnly", "true", archive);
        string[] args = ["backlog-similar", "quartz"];
        var before = Run(args, fixture.Workspace, false);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", before.Text);
        Assert.Equal(Run(args, fixture.Workspace, true).Text, before.Text);
        Assert.Equal(1, before.Count("similarity-projected"));

        // Keep the indexed row Completed so both query paths select this payload.
        fixture.ReplaceSnapshotMember(CompletedId, "Status", "\"Active\"", archive);
        AssertSimilarityDomainFailure(fixture, args, typeof(ArgumentException), "metadata");
    }

    [Fact]
    public async Task Similarity_ActiveHotStub_UsesValidCompletedArchive()
    {
        using var fixture = await Fixture.Create(4, 256);
        await fixture.MakeTerminalAndArchive(CompletedId);
        // Keep both the indexed Completed row and the completed archive unchanged.
        fixture.ReplaceSnapshotMember(CompletedId, "Status", "\"Active\"");
        string[] args = ["backlog-similar", "quartz", "--excerpt", "--limit", "100"];
        var baseline = Run(args, fixture.Workspace, true);
        Assert.Contains(CompletedId, baseline.Archived);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", baseline.Text);

        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(baseline.Text, candidate.Text);
        Assert.Equal(1, candidate.Count("similarity-projected"));
        Assert.Empty(candidate.Requested);
        Assert.Empty(candidate.Deserialized);
        Assert.Empty(candidate.Archived);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Similarity_MissingArchive_ValidatesHotStub(bool missingTable)
    {
        using var fixture = await Fixture.Create(4, 256);
        await fixture.MakeTerminalAndArchive(CompletedId);
        fixture.ExecuteState(missingTable
            ? "DROP TABLE goal_snapshot_archive"
            : $"DELETE FROM goal_snapshot_archive WHERE goal_id = '{CompletedId}'");
        string[] args = ["backlog-similar", "quartz"];
        var baseline = Run(args, fixture.Workspace, true);
        Assert.Empty(baseline.Archived);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", baseline.Text);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(baseline.Text, candidate.Text);
        Assert.Equal(1, candidate.Count("similarity-projected"));

        fixture.ReplaceSnapshotMember(CompletedId, "Status", "\"Active\"");
        AssertSimilarityDomainFailure(fixture, args, typeof(ArgumentException), "metadata");
    }

    [Fact]
    public async Task Similarity_MalformedHotStub_QuarantinesBeforeArchive()
    {
        using var fixture = await Fixture.Create(4, 256);
        const string validId = "55555555555555555555555555555555";
        await fixture.AddSimilarityTwin(validId);
        await fixture.MakeTerminalAndArchive(CompletedId);
        // The valid archive cannot hide a serializer failure in the hot stub.
        fixture.ReplaceSnapshotMember(CompletedId, "Objective", "42");
        string[] args = ["backlog-similar", "quartz"];
        Receipt baseline = null!, candidate = null!;
        var expectedError = AsyncLocalConsoleRouter.CaptureError(() => baseline = Run(args, fixture.Workspace, true));
        var actualError = AsyncLocalConsoleRouter.CaptureError(() => candidate = Run(args, fixture.Workspace, false));
        Assert.Contains($"QUARANTINED goal {CompletedId[..8]}: JsonException:", expectedError);
        Assert.Contains("$.Objective", expectedError);
        Assert.Equal(expectedError, actualError);
        Assert.Equal(baseline.Text, candidate.Text);
        Assert.DoesNotContain($"kind=goal id={CompletedId[..8]}", candidate.Text);
        Assert.Contains($"kind=goal id={validId[..8]}", candidate.Text);
        Assert.Equal(1, candidate.Count("similarity-projected"));
        Assert.DoesNotContain(CompletedId, baseline.Archived);
        Assert.Empty(candidate.Requested);
        Assert.Empty(candidate.Deserialized);
        Assert.Empty(candidate.Archived);
    }

    [Theory]
    [InlineData(false, GoalStatus.Completed)]
    [InlineData(true, GoalStatus.Completed)]
    [InlineData(false, GoalStatus.Failed)]
    [InlineData(true, GoalStatus.Failed)]
    [InlineData(false, GoalStatus.Cancelled)]
    [InlineData(true, GoalStatus.Cancelled)]
    [InlineData(false, GoalStatus.Superseded)]
    [InlineData(true, GoalStatus.Superseded)]
    public async Task Similarity_TerminalMetadata_PreservesCorpus(bool archive, GoalStatus status)
    {
        using var fixture = await Fixture.Create(4, 256);
        const string validId = "55555555555555555555555555555555";
        await fixture.AddSimilarityTwin(validId);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        fixture.ReplaceSnapshotMember(CompletedId, "IsMetadataOnly", "true", archive);
        fixture.ReplaceSnapshotMember(CompletedId, "Status", JsonSerializer.Serialize(status.ToString()), archive);

        string[] args = ["backlog-similar", "quartz", "--limit", "100"];
        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(baseline.Text, candidate.Text);
        Assert.Contains($"kind=goal id={validId[..8]}", candidate.Text);
        if (status == GoalStatus.Completed)
            Assert.Contains($"kind=goal id={CompletedId[..8]}", candidate.Text);
        else
            Assert.DoesNotContain($"kind=goal id={CompletedId[..8]}", candidate.Text);
        Assert.Equal(status == GoalStatus.Completed ? 2 : 1, candidate.Count("similarity-projected"));
        Assert.Empty(candidate.Requested);
        Assert.Empty(candidate.Deserialized);
        Assert.Empty(candidate.Archived);
    }

    [Theory]
    [InlineData(false, GoalStatus.Completed, GoalStatus.Active, GoalStatus.Parked)]
    [InlineData(true, GoalStatus.Completed, GoalStatus.Active, GoalStatus.Parked)]
    [InlineData(false, GoalStatus.Parked, GoalStatus.Active, GoalStatus.Parked)]
    [InlineData(true, GoalStatus.Parked, GoalStatus.Active, GoalStatus.Parked)]
    [InlineData(false, GoalStatus.Completed, GoalStatus.Cancelled, GoalStatus.Cancelled)]
    [InlineData(true, GoalStatus.Completed, GoalStatus.Cancelled, GoalStatus.Cancelled)]
    [InlineData(false, GoalStatus.Completed, GoalStatus.Superseded, GoalStatus.Superseded)]
    [InlineData(true, GoalStatus.Completed, GoalStatus.Superseded, GoalStatus.Superseded)]
    public async Task Similarity_ExplicitPark_PreservesEffectiveStatusCorpus(
        bool archive, GoalStatus storedStatus, GoalStatus retainedStatus, GoalStatus effectiveStatus)
    {
        using var fixture = await Fixture.Create(4, 256);
        const string validId = "55555555555555555555555555555555";
        await fixture.AddSimilarityTwin(validId);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        string[] args = ["backlog-similar", "quartz", "--limit", "100"];
        var before = Run(args, fixture.Workspace, false);
        Assert.Equal(Run(args, fixture.Workspace, true).Text, before.Text);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", before.Text);
        Assert.Equal(2, before.Count("similarity-projected"));

        fixture.ReplaceSnapshotMember(CompletedId, "ExplicitDisposition", """{"Revision":1,"Action":"Park"}""", archive);
        fixture.ReplaceSnapshotMember(CompletedId, "ParkedLifecycleStatus", JsonSerializer.Serialize(retainedStatus.ToString()), archive);
        fixture.ReplaceSnapshotMember(CompletedId, "Status", JsonSerializer.Serialize(storedStatus.ToString()), archive);
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath);
        var hydrated = await repository.LoadGoalsAsync([new GoalId(CompletedId)]);
        Assert.Equal(effectiveStatus, Assert.Single(hydrated.Goals).Status);

        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(baseline.Text, candidate.Text);
        Assert.DoesNotContain($"kind=goal id={CompletedId[..8]}", candidate.Text);
        Assert.Contains($"kind=goal id={validId[..8]}", candidate.Text);
        Assert.Equal(1, candidate.Count("similarity-projected"));
        Assert.Empty(candidate.Requested);
        Assert.Empty(candidate.Deserialized);
        Assert.Empty(candidate.Archived);
    }

    private static void AssertSimilarityDomainFailure(Fixture fixture, string[] args, Type exceptionType,
        string? parameter = null)
    {
        Exception? expected = null, actual = null;
        var expectedError = AsyncLocalConsoleRouter.CaptureError(() =>
            expected = Record.Exception(() => Run(args, fixture.Workspace, true)));
        var actualError = AsyncLocalConsoleRouter.CaptureError(() =>
            actual = Record.Exception(() => Run(args, fixture.Workspace, false)));
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(exceptionType, expected.GetType());
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
        if (expected is ArgumentException argument)
        {
            Assert.Equal(argument.ParamName, Assert.IsAssignableFrom<ArgumentException>(actual).ParamName);
            if (parameter is not null) Assert.Equal(parameter, argument.ParamName);
        }
        Assert.Empty(expectedError);
        Assert.Empty(actualError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Similarity_NullDocument_PreservesHydrationOutcome(bool archive)
    {
        using var fixture = await Fixture.Create(4, 256);
        const string validId = "55555555555555555555555555555555";
        await fixture.AddSimilarityTwin(validId);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        string[] args = ["backlog-similar", "quartz", "--limit", "100"];
        var before = Run(args, fixture.Workspace, false);
        Assert.Contains($"kind=goal id={CompletedId[..8]}", before.Text);
        Assert.Equal(2, before.Count("similarity-projected"));
        fixture.ReplaceSnapshotJson(CompletedId, "null", archive);

        if (archive)
        {
            NullReferenceException expected = null!, actual = null!;
            var expectedError = AsyncLocalConsoleRouter.CaptureError(() => expected = Assert.Throws<NullReferenceException>(
                () => Run(args, fixture.Workspace, true)));
            var actualError = AsyncLocalConsoleRouter.CaptureError(() => actual = Assert.Throws<NullReferenceException>(
                () => Run(args, fixture.Workspace, false)));
            Assert.Equal(expected.Message, actual.Message);
            Assert.Empty(expectedError);
            Assert.Empty(actualError);
        }
        else
        {
            Receipt baseline = null!, candidate = null!;
            var expectedError = AsyncLocalConsoleRouter.CaptureError(() => baseline = Run(args, fixture.Workspace, true));
            var actualError = AsyncLocalConsoleRouter.CaptureError(() => candidate = Run(args, fixture.Workspace, false));
            Assert.Contains($"QUARANTINED goal {CompletedId[..8]}: JsonException: deserialized goal snapshot was null", expectedError);
            Assert.Equal(expectedError, actualError);
            Assert.Equal(baseline.Text, candidate.Text);
            Assert.DoesNotContain($"kind=goal id={CompletedId[..8]}", candidate.Text);
            Assert.Contains($"kind=goal id={validId[..8]}", candidate.Text);
            Assert.Equal(1, candidate.Count("similarity-projected"));
            Assert.Empty(candidate.Requested);
            Assert.Empty(candidate.Deserialized);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Similarity_EqualTimelineInstants_PreservesLastOffsetDate(bool archive)
    {
        using var fixture = await Fixture.Create(4, 256);
        if (archive) await fixture.MakeTerminalAndArchive(CompletedId);
        var first = DateTimeOffset.Parse("2026-09-01T00:30:00+00:00", CultureInfo.InvariantCulture);
        var last = DateTimeOffset.Parse("2026-08-31T19:30:00-05:00", CultureInfo.InvariantCulture);
        Assert.Equal(first, last);
        Assert.NotEqual(first.Date, last.Date);
        fixture.ReplaceSnapshotMember(CompletedId, "Timeline", JsonSerializer.Serialize(new[]
        {
            new ProgressEventSnapshot(CompletedId, null, ProgressKind.GoalCreated, "first", first),
            new ProgressEventSnapshot(CompletedId, null, ProgressKind.GoalCreated, "last", last)
        }), archive);

        string[] args = ["backlog-similar", "quartz", "--excerpt", "--limit", "100"];
        var baseline = Run(args, fixture.Workspace, true);
        var candidate = Run(args, fixture.Workspace, false);
        Assert.Equal(baseline.Text, candidate.Text);
        var match = Assert.Single(candidate.Text.Split('\n'), line => line.Contains($"kind=goal id={CompletedId[..8]}"));
        Assert.Contains("2026-08-31", match);
        Assert.DoesNotContain("2026-09-01", match);
        Assert.Empty(candidate.Requested);
        Assert.Empty(candidate.Deserialized);
        Assert.Equal(1, candidate.Count("similarity-projected"));
    }

    private static Receipt Run(string[] args, OrchestratorWorkspace workspace, bool baseline)
    {
        var events = new List<GoalSnapshotReadObservation>();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath, events.Add);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var text = CaptureConsole(() =>
        {
            if (baseline)
            {
                // Exact eager entry setup from 7b393558; uses the same real handlers/renderers.
                CliCommandHelp.ThrowIfInvalidFlags(args);
                var ids = repository.ListGoalMetadataAsync().GetAwaiter().GetResult()
                    .Select(row => new GoalId(row.Id)).ToArray();
                var kernel = repository.LoadGoalsAsync(ids).GetAwaiter().GetResult();
                Goal? currentGoal = null;
                CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, null,
                    ref agents, ref profiles, ref currentGoal);
            }
            else
                CliBacklogQueryCommand.Execute(args, repository, workspace, providers, null, ref agents, ref profiles);
        });
        return new(text, events, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private void Report(string fixture, string arm, Receipt receipt) => output.WriteLine(JsonSerializer.Serialize(new
    {
        fixture, arm, requested = receipt.Requested, deserialized = receipt.Deserialized,
        archived = receipt.Archived, metadataCalls = receipt.Count("metadata"),
        relationshipCalls = receipt.Count("backlog-relationships"),
        projectionCalls = receipt.Count("similarity-metadata"), projectedDocuments = receipt.Count("similarity-projected"),
        materializedCharacters = receipt.Characters, callingThreadAllocatedBytes = receipt.AllocatedBytes
    }));

    private static string Normalize(string text)
    {
        if (!text.TrimStart().StartsWith('{')) return text;
        var node = JsonNode.Parse(text)!;
        node["generatedAt"] = "<timestamp>";
        return node.ToJsonString();
    }

    private sealed record Receipt(string Text, List<GoalSnapshotReadObservation> Events, long AllocatedBytes)
    {
        internal int Count(string operation) => Events.Count(evt => evt.Operation == operation);
        internal string[] Requested => Ids("requested");
        internal string[] Deserialized => Ids("hot-deserialized");
        internal string[] Archived => Ids("archive-deserialized");
        internal long Characters => Events.Where(evt => evt.Operation is "hot-deserialized" or "archive-deserialized"
            or "similarity-projected").Sum(evt => evt.MaterializedCharacters);
        private string[] Ids(string operation) => Events.Where(evt => evt.Operation == operation).Select(evt => evt.GoalId!).ToArray();
    }

    private sealed class Fixture(string root, OrchestratorWorkspace workspace, BacklogStore store) : IDisposable
    {
        internal OrchestratorWorkspace Workspace => workspace;
        internal BacklogStore Store => store;

        internal static async Task<Fixture> Create(int unrelatedCount, int payload)
        {
            var root = CreateTempDirectory();
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var store = new BacklogStore(workspace.BacklogStorePath);
            _ = await store.AddAsync("initialize", "initialize");
            var fixture = new Fixture(root, workspace, store);
            fixture.ExecuteBacklog("DELETE FROM backlog");
            fixture.ExecuteBacklog("""
                INSERT INTO backlog (id, title, body, status, created_at, updated_at)
                VALUES ('11111111111111111111111111111111', 'Selected quartz item', 'quartz query', 'Open',
                    '2026-09-01T00:00:00.0000000+00:00', '2026-09-01T00:00:00.0000000+00:00')
                """);
            await store.AddDependencyAsync(ItemId, new(DependencyId, BacklogDependencyTargetKind.Goal),
                goalExists: id => id == DependencyId);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId(OwnerId), "Selected owner");
            kernel.SetGoalSourceBacklogItemId(new GoalId(OwnerId), ItemId);
            kernel.CreateGoal(new GoalId(DependencyId), "Required dependency");
            kernel.CreateGoal(new GoalId(CompletedId), "Historical quartz match\nquartz body");
            for (var i = 0; i < unrelatedCount; i++)
                kernel.CreateGoal(new GoalId((10000 + i).ToString("x32", CultureInfo.InvariantCulture)), "Unrelated goal");
            var snapshot = kernel.ExportSnapshot();
            var goals = snapshot.Goals.Select(goal => goal with
            {
                Status = goal.Id == CompletedId ? GoalStatus.Completed : goal.Status,
                Tasks = goal.Tasks.Select((task, index) => task with
                {
                    Description = index == 0 && goal.Id != OwnerId && goal.Id != DependencyId
                        ? new string('x', payload) : task.Description
                }).ToArray(),
                Timeline = goal.Timeline.Select(evt => evt with { OccurredAt = Stamp }).ToArray(),
                BriefVersions = goal.BriefVersions?.Select(version => version with { RecordedAt = Stamp }).ToArray()
            }).ToArray();
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveGoalSnapshotsAsync(goals);
            return fixture;
        }

        internal async Task MakeTerminalAndArchive(params string[] ids)
        {
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var selected = await repository.LoadGoalsAsync(ids.Select(id => new GoalId(id)).ToArray());
            await repository.SaveGoalSnapshotsAsync(selected.ExportSnapshot().Goals.Select(goal => goal with
            {
                Status = GoalStatus.Completed,
                Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
                Timeline = goal.Timeline.Select(evt => evt with { OccurredAt = Stamp.AddDays(-2) }).ToArray()
            }).ToArray());
            Assert.Equal(ids.Length, await repository.ArchiveTerminalGoalSnapshotsAsync(Stamp, compressSnapshots: true));
        }

        internal async Task AddSimilarityTwin(string id)
        {
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var original = (await repository.LoadGoalsAsync([new GoalId(CompletedId)])).ExportSnapshot().Goals.Single();
            await repository.SaveGoalSnapshotsAsync([original with
            {
                Id = id,
                Timeline = original.Timeline.Select(evt => evt with { GoalId = id }).ToArray()
            }]);
        }

        internal void ReplaceTasks(string id, string tasks, bool archive = false)
            => ReplaceSnapshotMember(id, "Tasks", tasks, archive);

        internal void ReplaceSnapshotMember(string id, string member, string value, bool archive = false)
        {
            using var connection = Open(workspace.SqliteStatePath);
            using var read = connection.CreateCommand();
            read.CommandText = archive
                ? "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id"
                : "SELECT snapshot_json FROM goals WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            var stored = read.ExecuteScalar();
            Assert.NotNull(stored);
            var node = JsonNode.Parse(archive ? GoalSnapshotArchiveCodec.Decode(stored)! : (string)stored)!;
            Assert.Equal("Completed", node["Status"]!.GetValue<string>());
            if (member == "Tasks") Assert.IsType<JsonArray>(node[member]);
            node[member] = JsonNode.Parse(value);
            ReplaceSnapshotJson(id, node.ToJsonString(), archive);
        }

        internal void ReplaceSnapshotJson(string id, string json, bool archive = false)
        {
            if (archive)
                ReplaceArchive(id, GoalSnapshotArchiveCodec.Encode(json));
            else
            {
                using var connection = Open(workspace.SqliteStatePath);
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE goals SET snapshot_json = $json WHERE id = $id";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$json", json);
                Assert.Equal(1, update.ExecuteNonQuery());
            }
        }

        internal void ExecuteBacklog(string sql)
        {
            Execute(workspace.BacklogStorePath, sql);
        }

        internal void ExecuteState(string sql) => Execute(workspace.SqliteStatePath, sql);

        private static void Execute(string path, string sql)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal void ReplaceArchive(string id, byte[] bytes)
        {
            using var connection = Open(workspace.SqliteStatePath);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goal_snapshot_archive SET snapshot_json = $json WHERE goal_id = $id";
            command.Parameters.Add("$json", SqliteType.Blob).Value = bytes;
            command.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
