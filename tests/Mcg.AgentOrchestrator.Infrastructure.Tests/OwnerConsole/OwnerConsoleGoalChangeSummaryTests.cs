using System.Drawing;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

// Parallel-safe: instance-owned snapshots, I/O delegates, event gates and uninitialized GUI views.
public sealed class OwnerConsoleGoalChangeSummaryTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly string Candidate = new('a', 40);
    private static readonly string Basis = new('b', 40);
    private static readonly string Next = new('c', 40);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CommittedDiff_NulPathsPreserveKindsAndFailuresCannotLookEmpty()
    {
        var parsed = GoalChangesReader.ParseCommittedRange(new(0, "M\0src/長い😀.cs\0A\0name\nwith newline\0", ""));
        Assert.True(parsed.Available);
        Assert.Equal(new[] { "M src/長い😀.cs", "A name\nwith newline" }, parsed.Files);
        Assert.Equal(2, parsed.TotalFiles);
        Assert.True(GoalChangesReader.ParseCommittedRange(new(0, "", "")).Available);
        Assert.False(GoalChangesReader.ParseCommittedRange(new(128, "", "missing objects")).Available);
        Assert.False(GoalChangesReader.ParseCommittedRange(new(0, "M\0missing-path", "", DrainTimedOut: true)).Available);
        Assert.False(GoalChangesReader.ParseCommittedRange(new(0, "M\0", "")).Available);
    }

    [Fact]
    public async Task QueueAdapter_ProjectsSchedulingFactsWithoutPeerHydration()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < 4; index++)
                kernel.CreateGoal(new GoalId($"queue-{index}"), "Queue goal", [new(TaskId.New(), "Build", AgentRole.Developer)]);
            var original = kernel.ExportSnapshot();
            kernel.ReplaceWithSnapshot(original with { Goals = original.Goals.Select((goal, index) => goal with
            {
                Status = GoalStatus.Verified,
                Priority = index == 1 ? "High" : "Normal",
                PriorityScheduling = index == 2 ? new("Normal", GateOvertakes: 3) : null,
                CurrentHold = index == 3 ? new("hold", "waiting", "hold", At) : null,
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verified", At.AddMinutes(index))]
            }).ToArray() });
            var statePath = Path.Combine(root, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(statePath);
            var repository = new SqliteOrchestratorStateRepository(statePath);
            await repository.SaveAsync(kernel, Token);
            var facts = await SqliteOrchestratorStateRepository.OpenReadOnly(statePath).ListAcceptanceQueueFactsAsync(Token);
            var expected = GoalSchedulingOrder.ForAcceptance(kernel.Goals.Where(goal => goal.CurrentHold is null)).Select(goal => goal.Id.Value);
            var actual = facts.OrderByDescending(item => item.Priority).ThenBy(item => item.VerifiedAt).ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.GoalId, StringComparer.Ordinal).Select(item => item.GoalId);
            Assert.Equal(expected, actual);
            Assert.Equal(3, facts.Count);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("pending", "planning pending", "unavailable", false)]
    [InlineData("planned", "completed plan", "unavailable", false)]
    [InlineData("changed", "completed plan", "pending", true)]
    [InlineData("failed", "completed plan", "failed", true)]
    [InlineData("passed", "completed plan", "passed", true)]
    [InlineData("landed", "completed plan", "passed", true)]
    public async Task Lifecycle_QueryReachesSelectedPreviewAndDetail(
        string phase, string planned, string verified, bool changed)
    {
        var goal = Fixture(phase);
        var queries = new Queries(goal);
        var artifactReads = 0;
        var diffReads = 0;
        var promotionReads = 0;
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layoutComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summary = new OwnerGoalChangeSummaryQuery(async (id, token) =>
        {
            Assert.Equal(goal.Id.Value, id); promotionReads++;
            loadEntered.TrySetResult();
            // Without a UI dispatcher, publication runs inline on the loader thread.
            // Keep it behind this fixture's initial layout of the same preview label.
            await layoutComplete.Task.WaitAsync(token);
            return phase == "landed" ? Promotion(goal) : null;
        }, (basis, candidate, token) =>
        {
            Assert.Equal(Basis, basis); Assert.Equal(Candidate, candidate); diffReads++;
            return Task.FromResult(new CommittedGoalChanges(true, ["M src/Feature.cs", "A tests/FeatureTests.cs"], 2));
        }, (record, token) => { artifactReads++; return Task.FromResult(record.AuthoritativeStandardOutput); });
        var detailQuery = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, detailQuery);
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(Model(goal));
        var previewLoad = view.PreviewLoad;
        try
        {
            await TestHangGuard.WaitAsync(loadEntered.Task, "lifecycle summary load entered");
            Assert.False(previewLoad.IsCompleted);
            Assert.Contains("Planned: loading", view.PreviewText);
            view.Window.Layout(new Size(100, 40));
            Assert.False(previewLoad.IsCompleted);
            Assert.Same(previewLoad, view.PreviewLoad);
        }
        finally { layoutComplete.TrySetResult(); }
        await TestHangGuard.WaitAsync(previewLoad, "lifecycle preview publication after initial layout");

        Assert.Contains(planned, view.PreviewText);
        Assert.Contains(verified, view.PreviewText);
        Assert.Equal(phase == "pending" ? 0 : changed ? 2 : 1, artifactReads);
        Assert.Equal(changed ? 1 : 0, diffReads);
        Assert.Equal(1, promotionReads);
        Assert.Equal(new[] { goal.Id.Value }, queries.ReadIds);
        Assert.Equal(0, queries.MetadataReads);

        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        var text = Assert.Single(dialogs.Texts);
        Assert.StartsWith("11111111  Readable goal", text);
        Assert.True(text.IndexOf("Planned:", StringComparison.Ordinal) < text.IndexOf("Tasks:", StringComparison.Ordinal));
        Assert.Contains("Changed:", text); Assert.Contains("Reported:", text); Assert.Contains("Verified: " + verified, text);
        Assert.Contains("brief v1", text);
        Assert.Equal(changed ? 1 : 0, diffReads); // Immutable-range cache across preview/detail.
        if (changed)
        {
            Assert.Contains("M src/Feature.cs", text);
            Assert.Contains("worker-reported; not proof of behavior", text);
            Assert.Contains("Enable the feature", text);
            Assert.Contains(Candidate, text);
        }
        if (phase == "landed") Assert.Contains("Promotion: landed", text);
        else Assert.Contains("Promotion: landing not recorded", text);
    }

    [Theory]
    [InlineData(true, "Backlog slice: High priority: Fix the lane", "High priority: Fix the lane")]
    [InlineData(true, "Backlog slice: Backlog slice: Fix the lane", "Fix the lane")]
    [InlineData(false, "Backlog slice: Owner wording", "Backlog slice: Owner wording")]
    [InlineData(false, "Owner console: keep this colon", "Owner console: keep this colon")]
    public async Task BriefFallback_UsesReadableHeadingInSelectedPreviewAndDetail(
        bool generated, string heading, string expected)
    {
        var objective = "\r\n  " + heading + "\r\nBody" + (generated
            ? "\r\n" + BacklogIntakePlanner.TargetScopeHeadingLine + "\r\n" + BacklogIntakePlanner.UnknownScopeMarkerLine
            : "");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("11111111-summary"), objective,
            [new(TaskId.New(), "Build", AgentRole.Developer)]);

        var rendered = await RenderSelectedGoalAsync(new TestGoal(kernel.ExportGoalSnapshot(goal.Id)), new());

        Assert.Contains("Expected from brief — " + expected, rendered.Preview);
        Assert.Contains("  • " + expected + Environment.NewLine, rendered.Detail);
        Assert.Contains("  • Body" + Environment.NewLine, rendered.Detail);
        Assert.Contains("Source: authoritative brief v1", rendered.Detail);
        if (generated)
        {
            Assert.DoesNotContain(BacklogIntakePlanner.GeneratedHeadingPrefix, rendered.Preview);
            Assert.DoesNotContain(BacklogIntakePlanner.GeneratedHeadingPrefix, rendered.Detail);
        }
    }

    [Theory]
    [InlineData("passed", "passed")]
    [InlineData("gate-passed", "passed")]
    [InlineData("failed", "failed")]
    [InlineData("aborted:state-guard", "pending")]
    [InlineData("stale-candidate", "pending")]
    [InlineData("stale-main", "pending")]
    [InlineData("missing", "pending")]
    [InlineData("malformed", "pending")]
    [InlineData("oversized", "pending")]
    [InlineData("landed", "passed")]
    public async Task NormalAcceptanceWithoutObligations_ReachesSelectedPreviewAndDetail(string scenario, string expected)
    {
        var goal = Fixture(scenario == "landed" ? "landed" : "changed");
        goal = new TestGoal(goal.ToSnapshot() with { CriterionEvidenceObligations = null });
        Assert.Empty(((Goal)goal).CriterionEvidenceObligations);
        var directory = Path.Combine(Path.GetTempPath(), "mcg-summary-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "run-events.db");
            var store = new SqliteRunEventStore(path);
            // Unrelated goals and other main/candidate identities must never establish this pass.
            await AppendAcceptanceAsync(store, goal.Id, "passed", Next, Basis);
            await AppendAcceptanceAsync(store, goal.Id, "passed", Candidate, Next);
            await AppendAcceptanceAsync(store, new GoalId("unrelated"), "passed", Candidate, Basis);
            if (scenario is not ("stale-candidate" or "stale-main" or "missing"))
            {
                await AppendAcceptanceAsync(store, goal.Id, "gate-passed", Candidate, Basis);
                if (scenario is "malformed" or "oversized")
                    await store.AppendAsync(new(RunEventTypes.GoalOperation, goal.Id.Value, "acceptance", "Failed", null,
                        scenario == "malformed" ? "{invalid" : AcceptancePayload(goal.Id, "failed", Candidate, Basis,
                            new string('x', OwnerGoalChangeSummaryQuery.MaxArtifactBytes + 1)), At.AddMinutes(4)), Token);
                else if (scenario != "landed")
                    await AppendAcceptanceAsync(store, goal.Id, scenario, Candidate, Basis);
            }
            var reads = 0;
            var layoutComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summary = new OwnerGoalChangeSummaryQuery(
                promotion: (_, _) => Task.FromResult<GoalPromotionFact?>(scenario == "landed" ? Promotion(goal) : null),
                acceptance: async (id, candidate, landed, token) =>
                {
                    // This uninitialized app publishes inline on the loader thread. Finish the
                    // fixture's initial layout before allowing it to write the same preview label.
                    await layoutComplete.Task.WaitAsync(token);
                    reads++;
                    Assert.Equal(goal.Id, id);
                    Assert.Equal(Candidate, candidate);
                    if (scenario == "landed") Assert.Equal(Basis, landed!.PriorMainRevision);
                    return new OwnerGoalAcceptanceEvidence(Basis,
                        await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(path, id, candidate, Basis, token));
                });
            var queries = new Queries(goal);
            var detailQuery = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
            var harness = new OwnerConsoleHarness();
            var dialogs = new Dialogs();
            using var controller = Controller(harness, dialogs, detailQuery);
            using IApplication app = Terminal.Gui.App.Application.Create();
            using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
            view.Render(Model(goal));
            try { view.Window.Layout(new Size(100, 40)); }
            finally { layoutComplete.TrySetResult(); }
            await view.PreviewLoad.WaitAsync(Token);
            Assert.Contains("Verified: " + expected, view.PreviewText);
            await view.HandleKeyAsync(Key.Tab);
            await view.HandleKeyAsync(Key.Enter);
            var detail = Assert.Single(dialogs.Texts);
            Assert.Contains("Verified: " + expected, detail);
            Assert.Equal(2, reads);
            Assert.Equal(new[] { goal.Id.Value, goal.Id.Value }, queries.ReadIds);
            Assert.Equal(0, queries.MetadataReads);
            if (expected == "passed")
            {
                Assert.Contains("applicable full acceptance", detail);
                Assert.Contains("event ", detail);
                Assert.Contains("main " + Basis, detail);
            }
            else Assert.DoesNotContain("Verified: passed", detail);
            if (scenario is "malformed" or "oversized") Assert.Contains("Acceptance evidence unavailable", detail);
            Assert.Contains(scenario == "landed" ? "Promotion: landed" : "Promotion: landing not recorded", detail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NormalAcceptanceReceiptUpdates_InvalidatePassAtSameCandidate()
    {
        var goal = Fixture("changed");
        Assert.Empty(((Goal)goal).CriterionEvidenceObligations);
        var directory = Path.Combine(Path.GetTempPath(), "mcg-summary-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "run-events.db");
            var store = new SqliteRunEventStore(path);
            var summary = new OwnerGoalChangeSummaryQuery(acceptance: async (id, candidate, _, token) =>
                new OwnerGoalAcceptanceEvidence(Basis,
                    await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(path, id, candidate, Basis, token)));
            var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: summary);
            async Task AssertRenderedAsync(string state)
            {
                var detail = await query.LoadAsync(goal.Id.Value, Token);
                var output = new OwnerConsoleCapturedOutput();
                OwnerConsoleGoalDetailFormatter.Render(detail!, output, TimeZoneInfo.Utc);
                Assert.Contains("Verified: " + state, output.Text);
            }
            await AppendAcceptanceAsync(store, goal.Id, "gate-passed", Candidate, Basis);
            await AssertRenderedAsync("passed");
            // Equal timestamps exercise the journal's append-order tiebreak, without sleeps.
            await AppendAcceptanceAsync(store, goal.Id, "failed", Candidate, Basis);
            await AssertRenderedAsync("failed");
            await AppendAcceptanceAsync(store, goal.Id, "aborted:state-guard", Candidate, Basis);
            await AssertRenderedAsync("pending");
            await AppendAcceptanceAsync(store, goal.Id, "passed", Candidate, Basis);
            await AssertRenderedAsync("passed");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("passed", true)]
    [InlineData("aborted:state-guard", true)]
    [InlineData("passed", false)]
    public async Task NormalAcceptance_DoesNotEraseOutstandingCriterionEvidence(string outcome, bool failed)
    {
        var goal = Fixture("changed");
        var obligation = Obligation(Candidate, false);
        if (!failed) obligation = obligation with { State = CriterionEvidenceState.Pending, ReceiptId = null };
        goal = new TestGoal(goal.ToSnapshot() with { CriterionEvidenceObligations = [obligation] });
        var entry = new GoalOperationJournalEntry("acceptance-event", goal.Id, "acceptance",
            outcome == "passed" ? GoalOperationStatus.Completed : GoalOperationStatus.Aborted,
            At.AddMinutes(3), "full gate", Candidate, Basis, outcome);
        var summary = new OwnerGoalChangeSummaryQuery(acceptance: (_, _, _, _) =>
            Task.FromResult(new OwnerGoalAcceptanceEvidence(Basis, entry)));
        var detail = await new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: summary).LoadAsync(goal.Id.Value, Token);
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail!, output, TimeZoneInfo.Utc);
        Assert.Contains("Verified: " + (failed ? "failed" : "pending"), output.Text);
        Assert.DoesNotContain("Verified: passed", output.Text);
        Assert.Contains("Full acceptance: " + outcome, output.Text);
    }

    [Theory]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Tester, true)]
    [InlineData(AgentRole.Reviewer, true)]
    public async Task ReadOnlyDispatch_PreservesOnlyMatchingContributingRange(AgentRole role, bool advanced)
    {
        var original = Fixture("changed").ToSnapshot();
        var candidate = advanced ? Next : Candidate;
        var readOnly = original.Tasks.Single(task => task.RequiredRole == AgentRole.Developer) with
        {
            Id = TaskId.New().Value, RequiredRole = role, Status = WorkTaskStatus.Completed,
            LastVerification = null, VerificationHistory = null, DispatchHistory = null,
            LastDispatch = new("reader", "reader", "read-only-artifact", At.AddMinutes(2),
                BaseCommit: candidate, ResultCommit: candidate)
        };
        var goal = new TestGoal(original with { Tasks = [.. original.Tasks, readOnly] });
        var reads = new List<(string Base, string Candidate)>();
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, head, _) =>
        {
            reads.Add((basis, head));
            // Model the real empty result for HEAD..HEAD; the pre-fix selection loses the file.
            return Task.FromResult(basis == head ? new CommittedGoalChanges(true, [], 0) :
                new CommittedGoalChanges(true, ["M src/Feature.cs"], 1));
        });
        var (preview, detail) = await RenderSelectedGoalAsync(goal, query);
        Assert.Equal(new[] { (advanced ? Next : Basis, candidate) }, reads);
        Assert.Contains("Candidate: " + candidate, detail);
        Assert.Contains("Git " + (advanced ? Next : Basis) + ".." + candidate, detail);
        var expected = advanced ? "Changed: observed empty committed range" : "Changed: observed 1 files";
        Assert.Contains(expected, preview);
        Assert.Contains(expected, detail);
        if (advanced) Assert.DoesNotContain("M src/Feature.cs", detail);
        else
        {
            Assert.Contains("M src/Feature.cs", preview);
            Assert.Contains("M src/Feature.cs", detail);
            Assert.Contains("latest contributing dispatch range, not whole-goal coverage", detail);
        }
    }

    [Theory]
    [InlineData("current", "failed")]
    [InlineData("stale-main", "pending")]
    [InlineData("stale-candidate", "pending")]
    [InlineData("unknown-main", "pending")]
    [InlineData("main-unavailable", "pending")]
    [InlineData("current-pass", "passed")]
    [InlineData("landed", "failed")]
    public async Task RetainedFailure_RequiresExpectedMainInPreviewAndDetail(string scenario, string expected)
    {
        var original = Fixture("changed").ToSnapshot();
        var retainedCandidate = scenario == "stale-candidate" ? Next : Candidate;
        var retainedMain = scenario == "unknown-main" ? null : scenario is "stale-main" or "current-pass" ? Next : Basis;
        var goal = new TestGoal(original with
        {
            LatestAcceptanceFailure = new(At.AddMinutes(4), ["retained-check"], retainedCandidate, retainedMain)
        });
        Assert.NotNull(((Goal)goal).RetainedAcceptanceFailure);
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "run-events.db");
            var store = new SqliteRunEventStore(path);
            await AppendAcceptanceAsync(store, goal.Id, "failed", retainedCandidate, retainedMain ?? Next);
            if (scenario == "current-pass") await AppendAcceptanceAsync(store, goal.Id, "passed", Candidate, Basis);
            var reads = 0;
            var query = new OwnerGoalChangeSummaryQuery(
                promotion: (_, _) => Task.FromResult<GoalPromotionFact?>(scenario == "landed" ? Promotion(goal) : null),
                acceptance: async (id, candidate, landed, token) =>
                {
                    reads++;
                    Assert.Equal(goal.Id, id); Assert.Equal(Candidate, candidate);
                    if (scenario == "main-unavailable") throw new IOException("Integration revision unavailable.");
                    var main = landed?.PriorMainRevision ?? Basis;
                    return new OwnerGoalAcceptanceEvidence(main,
                        await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(path, id, candidate, main, token));
                });
            var (preview, detail) = await RenderSelectedGoalAsync(goal, query);
            Assert.Equal(2, reads);
            Assert.Contains("Verified: " + expected, preview);
            Assert.Contains("Verified: " + expected, detail);
            Assert.DoesNotContain("Landing: last gate failed", detail);
            if (expected == "failed") Assert.Contains("retained-check", detail);
            else
            {
                Assert.DoesNotContain("Verified: failed", detail);
                Assert.DoesNotContain("retained-check", detail);
            }
            if (scenario is "stale-main" or "current-pass")
            {
                Assert.Contains("Retained acceptance failure superseded", detail);
                Assert.Contains("recorded main " + Next + "; expected main " + Basis, detail);
            }
            if (scenario == "main-unavailable") Assert.Contains("Retained acceptance failure applicability unavailable", detail);
            if (scenario == "landed") Assert.Contains("Promotion: landed", detail);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static async Task<(string Preview, string Detail)> RenderSelectedGoalAsync(
        TestGoal goal, OwnerGoalChangeSummaryQuery summary)
    {
        var queries = new Queries(goal);
        var detailQuery = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, detailQuery);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(Model(goal));
        view.Window.Layout(new Size(100, 40));
        await view.PreviewLoad.WaitAsync(Token);
        var preview = view.PreviewText;
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        Assert.Equal(new[] { goal.Id.Value, goal.Id.Value }, queries.ReadIds);
        Assert.Equal(0, queries.MetadataReads);
        Assert.Empty(harness.Answers.Calls); Assert.Empty(harness.Conductor.Calls);
        return (preview, Assert.Single(dialogs.Texts));
    }

    private static Task<RunEventRecord> AppendAcceptanceAsync(SqliteRunEventStore store, GoalId id,
        string outcome, string candidate, string main) => store.AppendAsync(new(RunEventTypes.GoalOperation,
        id.Value, "acceptance", outcome == "failed" ? "Failed" : "Completed", "full gate",
        AcceptancePayload(id, outcome, candidate, main), At.AddMinutes(3)), Token);

    private static string AcceptancePayload(GoalId id, string outcome, string candidate, string main, string? detail = null)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Serialize(new GoalOperationJournalEntry(Guid.NewGuid().ToString("N"), id, "acceptance",
            outcome == "failed" ? GoalOperationStatus.Failed : GoalOperationStatus.Completed, At.AddMinutes(3),
            detail ?? "full gate", candidate, main, outcome), options);
    }

    [Theory]
    [InlineData("amended")]
    [InlineData("criteria")]
    [InlineData("missing")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("mapping")]
    public async Task InvalidPlan_FallsBackWithCurrentAuthority(string fault)
    {
        var goal = Fixture("planned");
        var snapshot = goal.ToSnapshot();
        if (fault == "amended")
            snapshot = snapshot with { Objective = "New brief", BriefVersions = [new(1, goal.Objective, At, SupersededByVersion: 2), new(2, "New brief", At.AddMinutes(1))] };
        if (fault == "criteria")
            snapshot = snapshot with { Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Planner ? task : task with
                { LastVerification = task.LastVerification! with { AcceptanceCriteriaVersionHash = "old" } }).ToArray() };
        goal = new TestGoal(snapshot);
        var reads = 0;
        var query = new OwnerGoalChangeSummaryQuery(artifact: (record, token) =>
        {
            reads++;
            return Task.FromResult(fault switch
            {
                "missing" => null,
                "oversized" => new string('x', OwnerGoalChangeSummaryQuery.MaxArtifactBytes + 1),
                "malformed" => "not a durable plan",
                "mapping" => Plan.Replace("1. disposition", "9. disposition", StringComparison.Ordinal),
                _ => Plan
            });
        });
        var detail = await new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: query).LoadAsync(goal.Id.Value, Token);
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail!, output, TimeZoneInfo.Utc);
        Assert.Contains("Expected from brief", output.Text);
        Assert.DoesNotContain("completed plan", output.Text);
        Assert.Contains("planning " + (fault is "amended" or "criteria" or "mapping" ? "stale" : "unavailable"), output.Text);
        Assert.Equal(fault is "amended" or "criteria" ? 0 : 1, reads);
    }

    [Fact]
    public async Task CandidateAndReceiptUpdates_InvalidateSectionsWithoutOldGreen()
    {
        var goal = Fixture("passed");
        var queries = new Queries(goal);
        var comparisons = new List<string>();
        var summary = new OwnerGoalChangeSummaryQuery(changes: (basis, candidate, token) =>
        {
            comparisons.Add(candidate);
            return Task.FromResult(new CommittedGoalChanges(true, [], 0));
        });
        var query = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
        Assert.StartsWith("passed", (await query.LoadSummaryAsync(goal.Id.Value, Token))!.Verified.State);
        var snapshot = goal.ToSnapshot();
        queries.Goal = new TestGoal(snapshot with { Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Developer ? task : task with
            { LastDispatch = task.LastDispatch! with { ResultCommit = Next }, DispatchHistory = null }).ToArray() });
        var updated = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Equal(Next, updated.Candidate);
        Assert.StartsWith("pending", updated.Verified.State);
        Assert.StartsWith("unavailable", updated.Reported.State);
        Assert.Equal(new[] { Candidate, Next }, comparisons);
        snapshot = queries.Goal.ToSnapshot();
        queries.Goal = new TestGoal(snapshot with { CriterionEvidenceObligations = [Obligation(Next, true)] });
        Assert.StartsWith("passed", (await query.LoadSummaryAsync(goal.Id.Value, Token))!.Verified.State);
        Assert.Equal(2, comparisons.Count); // A receipt-only update doesn't re-run Git.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportedSummary_UsesBoundAutoCommitAndRejectsContradictions(bool autoCommit)
    {
        var goal = Fixture("changed");
        var snapshot = goal.ToSnapshot();
        goal = new TestGoal(snapshot with { Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Developer ? task : task with
        {
            LastVerification = task.LastVerification! with
                { StandardOutput = task.LastVerification.StandardOutput.Replace("commit: " + Candidate, "commit: " + (autoCommit ? "none" : Next), StringComparison.Ordinal) }
        }).ToArray() });
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail());
        var summary = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.StartsWith(autoCommit ? "worker-reported" : "unavailable", summary.Reported.State);
        if (autoCommit) Assert.Contains(Candidate, summary.Reported.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiffAvailability_EmptyAndUnavailableStayDistinct(bool available)
    {
        var goal = Fixture("changed");
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(available, [], 0, "objects missing after cleanup"))));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Contains(available ? "empty committed range" : "unavailable", result.Changed.State);
        Assert.DoesNotContain("passed", result.Verified.State);
    }

    [Fact]
    public async Task Cleanup_UsesPromotionAndKeepsMissingVerificationExplicit()
    {
        var goal = Fixture("changed");
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(
            (_, _) => Task.FromResult<GoalPromotionFact?>(Promotion(goal)),
            (_, _, _) => Task.FromResult(new CommittedGoalChanges(false, [], 0, "objects unavailable"))));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.StartsWith("landed", result.Landing);
        Assert.StartsWith("unavailable", result.Changed.State);
        Assert.StartsWith("pending", result.Verified.State);
    }

    [Fact]
    public async Task Bounds_LimitFilesBulletsAndArtifactReads()
    {
        var goal = Fixture("changed");
        var reads = 0;
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(true, Enumerable.Range(0, 100).Select(i => $"M src/{i}.cs").ToArray(), 150)),
            artifact: (record, token) => { reads++; return Task.FromResult(record.AuthoritativeStandardOutput); }));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Equal(20, result.Changed.Items.Count);
        Assert.Equal(130, result.Changed.Omitted);
        Assert.InRange(result.Planned.Items.Count, 1, 5);
        Assert.Equal(2, reads);
        Assert.Contains("130 more", string.Join('\n', result.DetailLines()));
    }

    [Fact]
    public async Task SixMappedDeliverables_ShowFiveAndAnOmission()
    {
        var goal = Fixture("planned");
        var criteria = Enumerable.Range(1, 6).Select(index => $"Deliver behavior {index}").ToArray();
        var spec = new RefinedSpec("Expected feature", criteria, VerificationClass.TestVerifiable, [], []);
        var snapshot = goal.ToSnapshot();
        goal = new TestGoal(snapshot with
        {
            RefinedSpecVersions = [snapshot.RefinedSpecVersions![0] with { Spec = snapshot.RefinedSpecVersions[0].Spec with { AcceptanceCriteria = criteria } }],
            Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Planner ? task : task with
                { LastVerification = task.LastVerification! with { AcceptanceCriteriaVersionHash = EffectiveAcceptanceCriteriaVersion.ComputeHash(spec, []) } }).ToArray()
        });
        var plan = "<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->\n## Acceptance criteria mapping\n" +
            string.Join('\n', criteria.Select((criterion, index) => $"{index + 1}. disposition=planned; plan={criterion}; owner=Acceptance")) +
            "\n## Target seams and symbols\nsrc/Feature.cs\n<!-- MCG_DURABLE_PLANNER_PLAN:END -->";
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(artifact: (_, _) => Task.FromResult<string?>(plan)));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Equal("completed plan", result.Planned.State);
        Assert.Equal(5, result.Planned.Items.Count);
        Assert.Equal(1, result.Planned.Omitted);
        Assert.Contains("criteria 5", result.Planned.Items.Last());
    }

    [Fact]
    public async Task ArtifactCancellation_StopsBeforeDiffAndForwardsTheToken()
    {
        var goal = Fixture("changed");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var diffReads = 0;
        CancellationToken received = default;
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(
            changes: (_, _, _) => { diffReads++; return Task.FromResult(new CommittedGoalChanges(true, [], 0)); },
            artifact: (_, token) => { received = token; entered.SetResult(); return release.Task; }));
        var load = query.LoadSummaryAsync(goal.Id.Value, cancel.Token);
        await entered.Task.WaitAsync(Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.True(received.IsCancellationRequested);
        Assert.Equal(0, diffReads);
        release.SetResult(Plan);
    }

    [Fact]
    public async Task DetailRefreshFailure_MarksPriorSummaryStale()
    {
        var goal = Fixture("changed");
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail());
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render((await query.LoadAsync(goal.Id.Value, Token))!, output, TimeZoneInfo.Utc);
        var harness = new OwnerConsoleHarness();
        using var controller = Controller(harness, new Dialogs(), query);
        using var dialog = new OwnerConsoleGoalDialog(controller, new(output.Text, [], [], null, null, null),
            _ => Task.FromException<OwnerConsoleGoalDialog.Content?>(new IOException("offline")),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, Token);
        dialog.Start(action => action());
        controller.Apply(Model(goal));
        await dialog.LastLoad.WaitAsync(Token);
        Assert.Contains(dialog.Page.Lines, line => line.Contains("Verified: [stale", StringComparison.Ordinal));
        Assert.Contains(dialog.Page.Lines, line => line.Contains("Changed: [stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Selection_CancelsAndRejectsLateResultsAndDisposal()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        CancellationToken firstToken = default;
        using var session = new OwnerConsoleSelectedGoalSummary((id, token) =>
        {
            if (id == "first") { firstToken = token; firstEntered.SetResult(); return first.Task; }
            return Task.FromResult<OwnerGoalChangeSummary?>(Summary(id));
        }, action => { posts.Enqueue(action); posted.TrySetResult(); }, applied.Add, Token);
        session.Select("first");
        await firstEntered.Task.WaitAsync(Token);
        session.Select("second");
        await posted.Task.WaitAsync(Token);
        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(posts.TryDequeue(out var post)); post();
        await session.LastLoad.WaitAsync(Token);
        first.SetResult(Summary("first"));
        Assert.Equal("second", applied.Last().Summary!.GoalId);
        session.Select("third");
        var loading = session.LastLoad;
        session.Dispose();
        while (posts.TryDequeue(out var queued)) queued();
        await loading.WaitAsync(Token);
        Assert.DoesNotContain(applied, item => item.Summary?.GoalId == "third");
    }

    [Fact]
    public async Task RefreshDuringLoad_CoalescesAndMarksInterimContentStale()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        CancellationToken firstToken = default;
        var reads = 0;
        using var session = new OwnerConsoleSelectedGoalSummary((id, token) =>
        {
            if (++reads == 1) { firstToken = token; entered.SetResult(); return first.Task; }
            nextEntered.SetResult(); return next.Task;
        }, action => action(), preview =>
        {
            applied.Add(preview);
            if (preview.Summary?.Planned.State.StartsWith("[stale", StringComparison.Ordinal) == true)
                staleApplied.TrySetResult();
        }, Token);
        session.Select("selected");
        await entered.Task.WaitAsync(Token);
        session.Select("selected", refresh: true);
        session.Select("selected", refresh: true);
        Assert.False(firstToken.IsCancellationRequested);
        first.SetResult(Summary("selected"));
        await staleApplied.Task.WaitAsync(Token);
        await nextEntered.Task.WaitAsync(Token);
        Assert.StartsWith("[stale", applied.Last().Summary!.Verified.State);
        next.SetResult(Summary("selected"));
        await session.LastLoad.WaitAsync(Token);
        Assert.Equal(2, reads);
        Assert.Equal("pending", applied.Last().Summary!.Verified.State);
    }

    [Theory]
    [InlineData(80, 25)]
    [InlineData(100, 40)]
    public async Task ProductionViews_ResizeScrollNavigateAndReachCloseEpic(int width, int height)
    {
        var goal = Fixture("changed");
        var queries = new Queries(goal);
        var longPath = "M src/" + string.Concat(Enumerable.Repeat("長い😀/", 150)) + "Feature.cs";
        var query = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: new(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(true, [longPath], 1))));
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, query);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(Model(goal)); view.Window.Layout(new Size(width, height));
        await view.PreviewLoad.WaitAsync(Token);
        Assert.Contains("Planned:", view.PreviewText); Assert.Contains("Changed:", view.PreviewText);
        Assert.All(view.PreviewText.Split('\n'), line => Assert.True(line.GetColumns() <= width - 2));
        var reads = queries.ReadIds.Count;
        view.Window.Layout(new Size(width == 80 ? 100 : 80, height == 25 ? 40 : 25));
        Assert.Equal(reads, queries.ReadIds.Count);
        await view.HandleKeyAsync(Key.Tab); await view.HandleKeyAsync(Key.Enter);
        var detailText = Assert.Single(dialogs.Texts);
        Assert.Contains(longPath, detailText);
        using var goalDialog = new OwnerConsoleGoalDialog(controller,
            new(detailText, [], [], null, null, "epic"), _ => Task.FromResult<OwnerConsoleGoalDialog.Content?>(null),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, Token);
        var closed = 0;
        var production = goalDialog.CreateView(() => closed++, key => goalDialog.HandleKeyAsync(key));
        using var dialog = production.Dialog;
        foreach (var size in new[] { new Size(width, height), new Size(80, 25), new Size(100, 40) })
        {
            dialog.Layout(size); dialog.Render();
            Assert.True(dialog.Body.Viewport.Height > 0);
            Assert.All(dialog.Buttons.Where(button => button.Visible), button =>
            {
                Assert.True(button.Frame.X >= 0);
                Assert.True(button.Frame.Right <= button.SuperView!.Viewport.Width);
                Assert.True(button.Frame.Bottom <= button.SuperView.Viewport.Height);
            });
            Assert.Contains("Epic (e)", production.Buttons['e'].Text);
            // Exercise the production page's supported keys through the entire expanded text.
            for (var step = 0; step < goalDialog.Page.Lines.Count && goalDialog.Page.Selected < goalDialog.Page.Lines.Count - 1; step++)
                Assert.True(dialog.HandleKey(new Key(Key.PageDown)));
            Assert.Equal(goalDialog.Page.Lines.Count - 1, goalDialog.Page.Selected);
            Assert.True(goalDialog.Page.Offset > 0);
            Assert.Equal(goalDialog.Page.Lines.Last(), goalDialog.Page.Visible.Last());
            for (var step = 0; step < goalDialog.Page.Lines.Count && goalDialog.Page.Selected > 0; step++)
                Assert.True(dialog.HandleKey(new Key(Key.PageUp)));
            Assert.Equal(0, goalDialog.Page.Selected);
            Assert.Equal(0, goalDialog.Page.Offset);
        }
        production.Buttons['e'].InvokeCommand(Command.Accept);
        Assert.Equal("epic", goalDialog.RequestedEpicId);
        dialog.Buttons.Single(button => button.Text == "Close").InvokeCommand(Command.Accept);
        Assert.Equal(1, closed);
        Assert.True(await goalDialog.HandleKeyAsync('q'));
        Assert.Empty(harness.Answers.Calls); Assert.Empty(harness.Conductor.Calls);
    }

    private const string Plan = "<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->\n## Acceptance criteria mapping\n" +
        "1. disposition=planned; plan=Deliver expected behavior; owner=Acceptance\n" +
        "## Target seams and symbols\nsrc/Feature.cs\n<!-- MCG_DURABLE_PLANNER_PLAN:END -->";

    private static TestGoal Fixture(string phase)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("11111111-summary"), "Readable goal\nExpected feature",
            [new(TaskId.New(), "Plan", AgentRole.Planner), new(TaskId.New(), "Build", AgentRole.Developer)]);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var spec = new RefinedSpec("Expected feature", ["Deliver expected behavior"], VerificationClass.TestVerifiable, [], []);
        var hash = EffectiveAcceptanceCriteriaVersion.ComputeHash(spec, []);
        var changed = phase is "changed" or "failed" or "passed" or "landed";
        var worker = $"WORKER_RESULT:\nfiles: src/Feature.cs\ncommands: build\ntests: deferred - FeatureTests\ncommit: {Candidate}\n" +
            "blockers: none\nassigned_scope_complete: true\nmodel_fit: adequate\nskills: none\nconfidence: high\nsummary: Enable the feature\nEND_WORKER_RESULT";
        var tasks = snapshot.Tasks.Select(task => task with
        {
            Status = task.RequiredRole == AgentRole.Planner && phase != "pending" ? WorkTaskStatus.Completed : WorkTaskStatus.Assigned,
            LastDispatch = task.RequiredRole == AgentRole.Planner && phase != "pending" || task.RequiredRole == AgentRole.Developer && changed
                ? new TaskDispatchSnapshot("worker", "worker", "retained-artifact", At, BaseCommit: Basis,
                    ResultCommit: task.RequiredRole == AgentRole.Developer ? Candidate : null) : null,
            LastVerification = task.RequiredRole == AgentRole.Planner && phase != "pending" || task.RequiredRole == AgentRole.Developer && changed
                ? new TaskVerificationSnapshot("worker", "retained-artifact", 0, task.RequiredRole == AgentRole.Planner ? Plan : worker,
                    "", At.AddMinutes(1), DispatchStartedAt: At, AcceptanceCriteriaVersionHash: hash,
                    // Match persisted evidence: the complete authoritative stream equals StandardOutput.
                    AuthoritativeStandardOutputUnavailableReason: VerificationSnapshotText.StoredAsBoundedText) : null
        }).ToArray();
        return new TestGoal(snapshot with
        {
            Status = phase == "landed" ? GoalStatus.Completed : GoalStatus.Active, Tasks = tasks,
            RefinedSpecVersions = [new(1, new("Expected feature", ["Deliver expected behavior"], "TestVerifiable", [], []), At, 1)],
            CriterionEvidenceObligations = phase is "failed" or "passed" or "landed" ? [Obligation(Candidate, phase != "failed")] : null
        });
    }

    private static CriterionEvidenceObligation Obligation(string candidate, bool passed) => new(
        "criterion-v1-0", 0, 1, "Deliver expected behavior", CriterionEvidenceOwner.Acceptance,
        passed ? CriterionEvidenceState.Satisfied : CriterionEvidenceState.Failed,
        CriterionEvidenceScopes.FullAcceptanceGate, "acceptance receipt", At.AddMinutes(2),
        candidate, "receipt-1", "executed acceptance", ExpectedCandidateSha: candidate);

    private static GoalPromotionFact Promotion(Goal goal) => new(goal.Id.Value, Candidate, Basis, Next, At, GoalPromotionFact.SoloProvenance);
    private static OwnerGoalChangeSummary Summary(string id) => new(id, 1, "criteria", null,
        new("pending", [], "brief"), new("unavailable", [], "Git"), new("unavailable", [], "worker"),
        new("pending", [], "receipts"), "landing not recorded");
    private static OwnerConsoleViewModel Model(Goal goal) => new(new(false, 1, 0, 0, null, 0), [],
        [new("11111111", "Readable epic", "Readable goal", "Active", "Developer", null, goal.Id.Value)], []);
    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs, OwnerGoalDetailQuery query) =>
        OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs, harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock, goalDetail: query);

    private sealed class TestGoal(GoalSnapshot snapshot)
    {
        private readonly Goal _goal = AgentOrchestratorKernel.FromSnapshot(new([snapshot], [])).Goals.Single();
        internal GoalId Id => _goal.Id;
        internal string Objective => _goal.Objective;
        internal GoalSnapshot ToSnapshot() => snapshot;
        public static implicit operator Goal(TestGoal fixture) => fixture._goal;
    }

    private sealed class Queries(TestGoal goal) : IOrchestratorStateQueries
    {
        internal TestGoal Goal = goal;
        internal List<string> ReadIds = [];
        internal int MetadataReads;
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Single(ids); Assert.Equal(Goal.Id, ids.Single()); ReadIds.Add(ids.Single().Value);
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(new([Goal.ToSnapshot()], [])));
        }
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        { MetadataReads++; throw new InvalidOperationException("Unrelated metadata read"); }
    }
    private sealed class EmptyTail : IGoalEventTail
    { public IReadOnlyList<string> ReadLast(string id, int count) => []; }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal List<string> Texts = [];
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("No writes");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("No writes");
    }
}
