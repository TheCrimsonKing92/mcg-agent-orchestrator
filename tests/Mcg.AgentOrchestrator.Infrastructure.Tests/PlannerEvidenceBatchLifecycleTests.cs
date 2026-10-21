using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its SQLite database, worker artifacts and controlled gate receipts.
public sealed class PlannerEvidenceBatchLifecycleTests
{
    [Fact]
    public async Task MixedDispatch_ReloadAndAnswer_PreserveEveryIndependentRequirement()
    {
        using var f = new Fixture();
        var extraHistory = PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest.Replace("history", "second-history", StringComparison.Ordinal);
        var batch = PlannerMultipleEvidenceRequestMergeTests.MixedRequests + "\n" + extraHistory;
        f.Refresh(batch);
        Assert.True(f.Planner.LastVerification!.Succeeded,
            $"{f.Planner.LastVerification.OrchestratorFailureReason}\n{f.Planner.LastVerification.StandardError}");
        Assert.Equal(f.Planner.LastDispatch!.DispatchedAt, f.Planner.LastVerification.DispatchAttemptAt);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
        Assert.Equal(3, f.Kernel.HumanInputRequests.Count);
        Assert.Single(f.Goal.GateTestObligations);
        Assert.Equal(2, f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id).Count);
        Assert.Equal(4, f.Planner.LastVerification!.PlannerEvidenceBatch!.Directives.Count);
        await f.Reload();
        var initial = f.Kernel.HumanInputRequests.Select(item => item.Id).ToArray();
        var histories = f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id).ToArray();
        Assert.All(histories, item => Assert.NotNull(item.PlannerIdentity));
        Assert.All(histories, item => Assert.Equal("events", item.PlannerEvidence!.Store));
        var receipt = f.Planner.LastVerification!;
        f.Kernel.RecordDispatchExecutionResult(f.Goal.Id, f.Planner.Id, receipt);
        Assert.Equal(initial.OrderBy(item => item.Value), f.Kernel.HumanInputRequests.Select(item => item.Id).OrderBy(item => item.Value));
        f.Kernel.SubmitHumanInput(histories[0].Id, "first retrieved receipt");
        Assert.Equal(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
        Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        f.Kernel.SubmitHumanInput(histories[1].Id, "second retrieved receipt");
        Assert.Empty(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        Assert.NotEqual(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
        var op = Assert.Single(f.Kernel.GetPendingHumanInput(f.Goal.Id));
        Assert.Equal("operator", op.EvidenceOwner);
        Assert.Single(f.Goal.GateTestObligations);
        await f.Reload();
        Assert.Equal(2, f.Kernel.HumanInputRequests.Count(item => item.IsCompleted));
        // A replay with a new verification timestamp must also remain resolved.
        f.Record(batch);
        Assert.Equal(3, f.Kernel.HumanInputRequests.Count);
        Assert.Equal(2, f.Kernel.HumanInputRequests.Count(item => item.IsCompleted));
        Assert.Empty(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        var brief = f.Kernel.BuildTaskBrief(f.Goal.Id, f.DeveloperId).Content;
        Assert.Contains("Independent Planner Evidence Requirements", brief);
        Assert.Contains("second-history", brief);
        Assert.Contains("owner=operator", brief);
        Assert.Contains("AlphaTests", brief);
        Assert.Contains("identity=", brief);
        using var attention = JsonDocument.Parse(AttentionAgentReadView.Render(
            f.Kernel.GetPendingHumanInput(f.Goal.Id), [], goals: f.Kernel.Goals));
        var payload = attention.RootElement.GetProperty("payload");
        var operatorRow = Assert.Single(payload.GetProperty("waits").EnumerateArray());
        Assert.Equal("operator", operatorRow.GetProperty("owner").GetString());
        Assert.Equal("post-implementation", operatorRow.GetProperty("availability").GetString());
        Assert.Equal(f.Goal.AuthoritativeBrief.Version, operatorRow.GetProperty("plannerIdentity").GetProperty("briefVersion").GetInt32());
        Assert.Equal(op.ResumeCommand, operatorRow.GetProperty("nextAction").GetString());
        var gateRow = Assert.Single(payload.GetProperty("gateEvidence").EnumerateArray());
        Assert.Equal("gate-first", gateRow.GetProperty("identity").GetProperty("evidenceKey").GetString());
        using var show = JsonDocument.Parse(AttentionShowAgentReadView.Render(f.Goal.Id.Value,
            f.Kernel.GetPendingHumanInput(f.Goal.Id), [], _ => null, goals: f.Kernel.Goals));
        var showPayload = show.RootElement.GetProperty("payload");
        Assert.Equal(operatorRow.GetRawText(), Assert.Single(showPayload.GetProperty("waits").EnumerateArray()).GetRawText());
        Assert.Equal(gateRow.GetRawText(), Assert.Single(showPayload.GetProperty("gateEvidence").EnumerateArray()).GetRawText());
    }

    [Theory]
    [InlineData("PLANNER_EVIDENCE_REQUEST: {bad}")]
    [InlineData("PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":3,\"evidence_key\":\"outside\",\"availability\":\"never-recorded\",\"needed\":\"evidence\",\"reason\":\"missing\"}")]
    public async Task InvalidFinalItem_LeavesNoPersistedPartialSet(string invalid)
    {
        using var f = new Fixture();
        f.Record(PlannerMultipleEvidenceRequestMergeTests.MixedRequests + "\n" + invalid);
        Assert.Empty(f.Kernel.HumanInputRequests);
        Assert.Empty(f.Goal.GateTestObligations);
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.False(f.Planner.LastVerification!.Succeeded);
        Assert.Equal(0, f.Planner.LastVerification.ExitCode);
        await f.Reload();
        Assert.Empty(f.Kernel.HumanInputRequests);
        Assert.Empty(f.Goal.GateTestObligations);
        Assert.False(f.Planner.LastVerification!.Succeeded);
        Assert.NotNull(f.Planner.LastVerification.OrchestratorFailureReason);
    }

    [Fact]
    public async Task OmissionAndCrossResponseConflict_PreserveStoredSiblings()
    {
        using var f = new Fixture();
        f.Record(PlannerMultipleEvidenceRequestMergeTests.MixedRequests);
        await f.Reload();
        var ids = f.Kernel.HumanInputRequests.Select(item => item.Id).OrderBy(item => item.Value).ToArray();
        f.Record(PlannerMultipleEvidenceRequestMergeTests.FirstRequest);
        Assert.Equal(ids, f.Kernel.HumanInputRequests.Select(item => item.Id).OrderBy(item => item.Value));
        Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        var newItem = PlannerMultipleEvidenceRequestMergeTests.OperatorRequest.Replace("live-check", "new-item", StringComparison.Ordinal);
        f.Record(newItem + "\n" + PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest.Replace("Read receipt", "Changed payload", StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.Equal(ids, f.Kernel.HumanInputRequests.Select(item => item.Id).OrderBy(item => item.Value));
        Assert.Single(f.Goal.GateTestObligations);
        await f.Reload();
        Assert.Equal(ids, f.Kernel.HumanInputRequests.Select(item => item.Id).OrderBy(item => item.Value));
        Assert.False(f.Planner.LastVerification!.Succeeded);
        f.Kernel.SubmitHumanInput(Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id)).Id, "retrieved history");
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.Single(f.Kernel.GetPendingHumanInput(f.Goal.Id));
        Assert.Single(f.Goal.GateTestObligations);
    }

    [Fact]
    public async Task LegacySingleIsUntyped_PluralWithoutDispatchBindingIsRejected()
    {
        using var f = new Fixture(legacy: true);
        f.Record(PlannerMultipleEvidenceRequestMergeTests.OperatorRequest, correlated: false);
        Assert.Null(Assert.Single(f.Kernel.HumanInputRequests).PlannerIdentity);
        await f.Reload();
        Assert.Null(Assert.Single(f.Kernel.HumanInputRequests).PlannerIdentity);
        f.Record(PlannerMultipleEvidenceRequestMergeTests.MixedRequests, correlated: false);
        Assert.Single(f.Kernel.HumanInputRequests);
        Assert.Empty(f.Goal.GateTestObligations);
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.Contains(f.Goal.Timeline, item => item.Message.Contains("authoritative dispatch brief provenance", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyAnswerAndOldBrief_CannotSuppressNewTypedRequirement()
    {
        using var f = new Fixture();
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest, AgentRole.Planner).Directive!;
        var legacy = f.Kernel.RequestHumanInputDeduplicated(f.Goal.Id, f.Planner.Id, parsed.Question,
            kind: parsed.Kind, questionFingerprint: parsed.QuestionFingerprint, blockerFingerprint: parsed.BlockerFingerprint).Request;
        f.Kernel.SubmitHumanInput(legacy.Id, "legacy answer");
        f.Record(PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest);
        var current = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        Assert.NotEqual(legacy.Id, current.Id);
        Assert.NotNull(current.PlannerIdentity);
        var oldVersion = f.Goal.AuthoritativeBrief.Version;
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "New authoritative planning brief");
        // A completion from the old dispatch cannot borrow the new version.
        f.Record(PlannerMultipleEvidenceRequestMergeTests.OperatorRequest);
        Assert.DoesNotContain(f.Kernel.HumanInputRequests, item => item.EvidenceOwner == "operator");
        f.Kernel.SubmitHumanInput(current.Id, "answer to old brief only");
        Assert.Equal(oldVersion, current.PlannerIdentity!.BriefVersion);
        Assert.NotEqual(WorkTaskStatus.Completed, f.Planner.Status);
        f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Plan against the new authoritative brief.");
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id,
            new TaskDispatchRecord("planner", "plan", f.Root, DateTimeOffset.UtcNow));
        f.Record(PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest);
        var next = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        Assert.Equal(f.Goal.AuthoritativeBrief.Version, next.PlannerIdentity!.BriefVersion);
        Assert.False(next.IsCompleted);
        Assert.NotEqual(current.Id, next.Id);
        Assert.Equal(3, f.Kernel.HumanInputRequests.Count);
    }

    [Fact]
    public async Task OldPendingHistory_AfterBriefChangeAndReload_DoesNotHoldCurrentCompletion()
    {
        using var f = new Fixture();
        f.Record(PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest);
        var old = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Plan fresh evidence for the current brief.");
        f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Replan the superseded brief.");
        Assert.False(old.IsCompleted);
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id,
            new TaskDispatchRecord("planner", "plan", f.Root, DateTimeOffset.UtcNow));
        f.Record(PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest);
        await f.Reload();
        Assert.Equal(2, f.Kernel.HumanInputRequests.Count);
        var current = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        Assert.NotEqual(old.Id, current.Id);
        f.Kernel.SubmitHumanInput(current.Id, "current brief receipt");
        Assert.Equal(WorkTaskStatus.Completed, f.Planner.Status);
        Assert.Equal(GoalStatus.Active, f.Goal.Status);
        Assert.False(f.Kernel.GetHumanInputRequest(old.Id).IsCompleted);
        Assert.Empty(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
    }

    [Fact]
    public void HistoricalAnswerAfterBriefRevision_DoesNotCompleteCurrentPlanning()
    {
        using var f = new Fixture();
        f.Record(PlannerMultipleEvidenceRequestMergeTests.HistoricalRequest);
        var history = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        Assert.True(f.Planner.LastVerification!.Succeeded);
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Current planning needs its own evidence.");
        f.Kernel.SubmitHumanInput(history.Id, "old brief answer only");
        Assert.True(history.IsCompleted);
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.Contains(f.Goal.Timeline, item => item.Message.Contains("Historical Planner answer retained", StringComparison.Ordinal));
        var brief = f.Kernel.BuildTaskBrief(f.Goal.Id, f.Planner.Id).Content;
        Assert.Contains("historical/unversioned", brief);
        Assert.DoesNotContain("## Resolved Human Input", brief);
    }

    [Fact]
    public void ObsoleteAttempt_CannotReplaceCurrentVerificationOrRequirements()
    {
        using var f = new Fixture();
        f.Record(PlannerMultipleEvidenceRequestMergeTests.FirstRequest);
        var old = f.Planner.LastVerification!;
        var id = Assert.Single(f.Goal.GateTestObligations).Id;
        f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Plan the remaining work.");
        var started = old.DispatchAttemptAt!.Value.AddSeconds(1);
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id, new TaskDispatchRecord("planner", "plan", f.Root, started));
        f.Kernel.RecordDispatchExecutionResult(f.Goal.Id, f.Planner.Id, old);
        Assert.Equal(started, f.Planner.LastDispatch!.DispatchedAt);
        Assert.Equal(WorkTaskStatus.Running, f.Planner.Status);
        Assert.Null(f.Planner.LastVerification);
        Assert.Equal(id, Assert.Single(f.Goal.GateTestObligations).Id);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public void MatchingAcceptanceReceipt_ClearsOnlyItsItemAndRequiresCurrentBriefAndCandidate()
    {
        using var f = new Fixture(revised: true);
        var selectorless = PlannerMultipleEvidenceRequestMergeTests.OperatorRequest.Replace("operator", "acceptance", StringComparison.Ordinal)
            .Replace("live-check", "manual-acceptance", StringComparison.Ordinal);
        f.Record(PlannerMultipleEvidenceRequestMergeTests.MixedRequests + "\n" + selectorless + "\n" + PlannerMultipleEvidenceRequestMergeTests.SecondRequest);
        var directory = Path.Combine(f.Root, ".orchestrator");
        var sha = new string('a', 40);
        var old = ProspectiveEvidenceGateFixture.WriteAttempt(directory, f.Goal, sha,
            [("AlphaTests.Check", "Passed"), ("BetaTests.Check", "Passed")]);
        Assert.All(GateTestObligationEvaluator.Evaluate(f.Goal, directory, sha), item => Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, item.Outcome));
        StampVersion(old.MetadataPath, f.Goal.AuthoritativeBrief.Version - 1);
        Assert.All(GateTestObligationEvaluator.Evaluate(f.Goal, directory, sha), item => Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, item.Outcome));
        StampVersion(old.MetadataPath, f.Goal.AuthoritativeBrief.Version);
        Assert.All(GateTestObligationEvaluator.Evaluate(f.Goal, directory, new string('b', 40)), item => Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, item.Outcome));
        var evidence = GateTestObligationEvaluator.Evaluate(f.Goal, directory, sha);
        Assert.Equal(2, evidence.Count);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, evidence.Single(item => item.Obligation.PlannerIdentity!.EvidenceKey == "gate-first").Outcome);
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, evidence.Single(item => item.Obligation.PlannerIdentity!.EvidenceKey == "gate-second").Outcome);
        var resolved = ProspectiveEvidenceGateResolver.Resolve(f.Kernel, f.Goal, directory, sha);
        Assert.Empty(resolved.ResolvedRequestIds);
        var pending = f.Kernel.GetPendingHumanInput(f.Goal.Id);
        Assert.Equal(3, pending.Count);
        var op = pending.Single(item => item.EvidenceOwner == "operator");
        var manual = pending.Single(item => item.EvidenceOwner == "acceptance-gate");
        Assert.Null(manual.TestNames);
        Assert.Throws<UnauthorizedAccessException>(() => f.Kernel.SubmitHumanInput(op.Id, "worker claim", origin: HumanInputAnswerOrigin.Worker));
        Assert.Throws<UnauthorizedAccessException>(() => f.Kernel.SubmitHumanInput(manual.Id, "empty/full gate passed", origin: HumanInputAnswerOrigin.Worker));
        Assert.All(pending, item => Assert.False(item.IsCompleted));
        f.Kernel.SubmitHumanInput(manual.Id, "authorized explicit acceptance evidence");
        Assert.False(op.IsCompleted);
        Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        f.Kernel.SubmitHumanInput(op.Id, "authorized live observation");
        Assert.True(op.IsCompleted);
        Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
    }

    [Fact]
    public async Task StaleSnapshotMerge_RetainsBatchAndCompletedAnswer()
    {
        using var f = new Fixture();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = Assert.Single(f.Kernel.ExportSnapshot().Goals);
        f.Record(PlannerMultipleEvidenceRequestMergeTests.MixedRequests);
        var tick = f.Kernel.ExportSnapshot();
        await f.Repository.TransactGoalAsync<bool>(f.Goal.Id, (stored, _) =>
            Task.FromResult((true, stored! with { OperatorAcceptanceRegateCount = 1 }, true)));
        var saved = await f.Repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline, Assert.Single(tick.Goals), tick.HumanInputRequests)]);
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, Assert.Single(saved).Disposition);
        f.Kernel = await new SqliteOrchestratorStateRepository(f.Database).LoadAsync();
        Assert.Equal(2, f.Kernel.HumanInputRequests.Count);
        Assert.Single(f.Goal.GateTestObligations);
        var history = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        f.Kernel.SubmitHumanInput(history.Id, "retrieved");
        await f.Repository.SaveAsync(f.Kernel);
        // Reconcile stale pending input against its stored answer, through the real repository merge.
        var current = Assert.Single(f.Kernel.ExportSnapshot().Goals);
        await f.Repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(Assert.Single(tick.Goals), current, tick.HumanInputRequests)]);
        f.Kernel = await new SqliteOrchestratorStateRepository(f.Database).LoadAsync();
        Assert.True(f.Kernel.GetHumanInputRequest(history.Id).IsCompleted);
        Assert.Single(f.Goal.GateTestObligations);
    }

    private static void StampVersion(string path, int version)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["briefVersion"] = version;
        File.WriteAllText(path, json.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("candidate")]
    [InlineData("main")]
    public async Task PendingAttribution_ReloadHoldsWithoutConsumingPlannerSiblings(string? mismatch)
    {
        using var f = new Fixture();
        f.Record(PlannerMultipleEvidenceRequestMergeTests.MixedRequests);
        var history = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        f.Kernel.SubmitHumanInput(history.Id, "retrieved historical receipt");
        Assert.Equal(WorkTaskStatus.Completed, f.Planner.Status);
        ConductorDriverTests.PassVerification(f.Kernel, f.Goal, f.Kernel.GetTask(f.Goal.Id, f.DeveloperId));
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        Assert.True(f.Kernel.BeginGoalAcceptanceVerification(f.Goal.Id, "controlled native attempt"));
        const string check = "infrastructure tests";
        var candidate = new string('a', 40);
        var main = new string('b', 40);
        var attributions = new[] { new AcceptanceCheckAttribution(check, AcceptanceFailureOrigin.Introduced,
            "candidate executed RED; baseline unavailable") };
        Assert.True(f.Kernel.ReconcileGoalAcceptanceFailed(f.Goal.Id, [check], "native RED",
            candidate, main, attributions, "baseline not executed"));
        var pending = new AcceptanceAttributionPending(candidate, main, "native-attempt",
            [new(check, "AlphaTests.Check", "baseline unavailable",
                [new("first.trx", "first-hash", "first-run")],
                [new("rerun.trx", "rerun-hash", "rerun-run")])], "none");
        f.Goal.RetainAcceptanceAttributionPending(pending);
        var occurrence = f.Goal.LatestAcceptanceFailure!.OccurredAt;
        if (mismatch is not null)
        {
            pending = mismatch == "candidate" ? pending with { CandidateSha = new string('c', 40) }
                : pending with { MainSha = new string('d', 40) };
            Assert.Throws<InvalidOperationException>(() => f.Goal.RetainAcceptanceAttributionPending(pending));
            // Restore deliberately retains historical facts. Its consumer must hold even a mismatched
            // marker, rather than turning it into authority to retry, land or consume sibling evidence.
            var snapshot = f.Kernel.ExportSnapshot();
            var saved = Assert.Single(snapshot.Goals);
            f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [saved with
                { LatestAcceptanceFailure = saved.LatestAcceptanceFailure! with { AttributionPending = pending } }] });
        }
        var before = f.Kernel.ExportSnapshot();
        var requests = JsonSerializer.Serialize(before.HumanInputRequests);
        var obligations = JsonSerializer.Serialize(Assert.Single(before.Goals).GateTestObligations);
        await f.Reload();
        var failure = f.Goal.LatestAcceptanceFailure!;
        Assert.Equal(occurrence, failure.OccurredAt);
        Assert.Equal(candidate, failure.BranchHeadSha);
        Assert.Equal(main, failure.MainHeadSha);
        Assert.Equal(attributions, failure.CheckAttributions);
        Assert.Equal("baseline not executed", failure.BaselineAttestation);
        Assert.Equal(JsonSerializer.Serialize(pending), JsonSerializer.Serialize(failure.AttributionPending));
        Assert.Equal(requests, JsonSerializer.Serialize(f.Kernel.ExportSnapshot().HumanInputRequests));
        Assert.Equal(obligations, JsonSerializer.Serialize(Assert.Single(f.Kernel.ExportSnapshot().Goals).GateTestObligations));

        var escalations = new List<string>();
        var driver = ConductorDriverTests.MakeDriver(
            runAcceptanceSummary: _ => throw new InvalidOperationException("Unexpected acceptance admission"),
            retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Unexpected Developer retry"),
            dispatchAndStart: _ => throw new InvalidOperationException("Unexpected worker dispatch"),
            writeEscalation: (_, _, reason) => escalations.Add(reason));
        var held = ConductorDriverTestsAttributionPending.AssertPending(
            driver.AdvanceOnce(f.Goal, Mcg.AgentOrchestrator.Core.Conductor.ConductorAutonomyPolicy.Conservative), f.Goal);
        Assert.Equal(pending.Identity, held.StableIdentity);
        Assert.Contains("--confirm-acceptance-retry", Assert.Single(escalations));
        Assert.True(f.Kernel.GetHumanInputRequest(history.Id).IsCompleted);
        var op = Assert.Single(f.Kernel.GetPendingHumanInput(f.Goal.Id));
        Assert.Equal("operator", op.EvidenceOwner);
        Assert.Equal("live-check", op.PlannerIdentity!.EvidenceKey);
        Assert.Single(f.Goal.GateTestObligations);
        Assert.Equal(requests, JsonSerializer.Serialize(f.Kernel.ExportSnapshot().HumanInputRequests));
        var brief = f.Kernel.BuildTaskBrief(f.Goal.Id, f.DeveloperId).Content;
        Assert.Contains("owner=operator", brief);
        Assert.Contains("AlphaTests", brief);
        Assert.Contains("history", brief);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-planner-batch-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Root, "state.db");
        internal SqliteOrchestratorStateRepository Repository => new(Database);
        internal AgentOrchestratorKernel Kernel { get; set; } = new();
        private readonly GoalId _goalId;
        private readonly TaskId _plannerId;
        internal TaskId DeveloperId { get; }
        internal Goal Goal => Kernel.GetGoal(_goalId);
        internal TaskSpec Planner => Kernel.GetTask(_goalId, _plannerId);
        private int _round;
        internal Fixture(bool legacy = false, bool revised = false)
        {
            Directory.CreateDirectory(Root);
            _ = StateDbMigrations.EnsureUpToDate(Database);
            var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
            var developer = new TaskSpec(TaskId.New(), "Implement the plan.", AgentRole.Developer);
            _plannerId = planner.Id;
            DeveloperId = developer.Id;
            var goal = Kernel.CreateGoal("Preserve independent Planner requirements", [planner, developer]);
            _goalId = goal.Id;
            Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Implement mixed evidence.",
                ["Acceptance executes candidate tests.", "Operator observes live behavior."], VerificationClass.TestVerifiable, [], []));
            if (revised) Kernel.ReviseGoalBrief(goal.Id, "Use the current candidate brief.");
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner", "plan", Root, DateTimeOffset.UtcNow));
            if (legacy)
            {
                var snapshot = Kernel.ExportSnapshot();
                var saved = snapshot.Goals.Single();
                saved = saved with { Tasks = saved.Tasks.Select(task => task.Id == planner.Id.Value ? task with
                    { LastDispatch = task.LastDispatch! with { BriefSnapshot = null }, DispatchHistory = task.DispatchHistory!.Select(item => item with { BriefSnapshot = null }).ToArray() } : task).ToArray() };
                Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [saved] });
            }
        }
        internal void Record(string batch, bool correlated = true)
        {
            var dispatch = Planner.LastDispatch!;
            Kernel.RecordDispatchExecutionResult(Goal.Id, Planner.Id, new TaskVerificationRecord(
                dispatch.Command, Root, 0, batch + "\n" + WorkerResult, "", DateTimeOffset.UtcNow.AddTicks(++_round),
                WorkerResultPresent: true, DispatchAttemptAt: correlated ? dispatch.DispatchedAt : null));
        }
        internal void Refresh(string batch)
        {
            File.WriteAllText(Path.Combine(Root, "seed.txt"), "seed");
            var stdout = Path.Combine(Root, "planner.out.log");
            var stderr = Path.Combine(Root, "planner.err.log");
            var exit = Path.Combine(Root, "planner.exit.txt");
            var plan = WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
                WorkerDispatchTestSupport.PlannerContractAcceptanceMappingBody,
                "1. disposition=planned; plan=Acceptance executes the candidate tests\n2. disposition=planned; plan=Operator observes the candidate", StringComparison.Ordinal);
            File.WriteAllText(stdout, WorkerDispatchTestSupport.ResearcherContractFixture() + "\n" + plan + "\n" + batch + "\n" + WorkerResult);
            File.WriteAllText(stderr, "");
            File.WriteAllText(exit, "0");
            Kernel.RecordTaskProcessStarted(Goal.Id, Planner.Id, new TaskProcessRecord(4245, "plan", Root, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null));
            var checkedLiveness = false;
            new BackgroundDispatchRunner(isStillRunning: _ => { checkedLiveness = true; return false; }).RefreshLatestProcess(Kernel, Goal.Id, Planner.Id);
            Assert.True(checkedLiveness);
        }
        internal async Task Reload()
        {
            await Repository.SaveAsync(Kernel);
            Assert.True(File.Exists(Database));
            Kernel = await new SqliteOrchestratorStateRepository(Database).LoadAsync();
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
        private const string WorkerResult = "WORKER_RESULT:\nfiles: none\ncommands: source inspection\ntests: not-run - Planner role\ncommit: none\nblockers: none\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
    }
}
