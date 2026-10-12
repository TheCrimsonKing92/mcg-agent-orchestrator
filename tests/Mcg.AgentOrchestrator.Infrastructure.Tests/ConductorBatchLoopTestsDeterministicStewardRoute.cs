using System.Text.Json;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsDeterministicStewardRoute : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsDeterministicStewardRoute(ITestOutputHelper output) : base(output) { }

    private const string PlanFailure = "[orchestrator Planner output contract rejection] acceptance criterion 1 with disposition=planned must include a non-empty plan";
    private const string MultipleRequests = "Malformed PLANNER_EVIDENCE_REQUEST: exactly one evidence request may be emitted per round.";
    private static readonly string[] RequiredSelections =
    [
        "ConductorStewardDeterministicRouteTests", "ConductorBatchLoopTestsDeterministicStewardRoute",
        "ConductorBatchLoopTestsStewardDispatchBounds", "ConductorBatchLoopTestsStewardHarvestSuperseded",
        "OperatorIntentAdjudicationTests", "PlannerEvidenceDispatchTests",
        "PlannerEvidenceRequestTestNamesTests", "PlannerEvidenceRequestCriterionNumberTests",
        "PlannerEvidenceRequestTestNamesTests.NamesAreNotNormalizedOrDeduplicated",
        "ConductorBatchLoopTestsDeterministicStewardRoute.Multiple_requests_route_once_through_conductor_and_deliver_complete_rejected_plan(count: 3)"
    ];
    private static readonly string[] Criteria = ["Acceptance proves the format recovery.", "Operator reviews the exact candidate with another model family."];

    [Xunit.Theory(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(3)]
    [Xunit.InlineData(4)]
    public async Task Multiple_requests_route_once_through_conductor_and_deliver_complete_rejected_plan(int count)
    {
        using var harness = new StewardHarness("B");
        var original = ResearchPlan() + "\n" + string.Join("\n", OriginalRequests(count));
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(original, AgentRole.Planner);
        Xunit.Assert.True(parsed.IsMalformed);
        Xunit.Assert.Null(parsed.Directive);
        Xunit.Assert.Equal(MultipleRequests, parsed.Diagnostic);
        Xunit.Assert.True(PlannerOutputContract.TryValidate(original, out _, out var diagnostic, Criteria), diagnostic);
        SeedPlanner(harness, "[orchestrator Planner output contract rejection]\n" + parsed.Diagnostic, original);
        var rejected = Xunit.Assert.Single(harness.Task.VerificationHistory);
        var originalBytes = File.ReadAllBytes(rejected.StandardOutputPath!);
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());

        var stopPath = Path.Combine(harness.Root, "stop.signal");
        new ConductorBatchLoop(operatorIntents: harness.Coordinator).WithSteward(host).Run(
            harness.Kernel, MakeDriver(), ConductorAutonomyPolicy.Conservative, stopPath,
            maxIterations: 3, watchInterval: TimeSpan.FromMilliseconds(1),
            sleepFunc: _ => false, keepAliveWhenIdle: true, persistGoalTick: (_, _) => { },
            onTick: _ =>
            {
                if (harness.Task.Status == WorkTaskStatus.Assigned) File.WriteAllText(stopPath, "stop");
            });
        host.ServiceTick(harness.Kernel);

        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        Xunit.Assert.Equal(0, harness.Model.Calls);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, harness.Task.Status);
        Xunit.Assert.Equal(RetryCause.ContractClarification, harness.Task.PendingRetryCause);
        Xunit.Assert.Single(harness.Goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried));
        Xunit.Assert.Same(rejected, Xunit.Assert.Single(harness.Task.VerificationHistory));
        Xunit.Assert.False(rejected.Succeeded);
        Xunit.Assert.Null(harness.Task.LastVerification);
        Xunit.Assert.Contains(MultipleRequests, harness.Task.AcceptedRetryFeedback?.Message);
        Xunit.Assert.Contains("Operator obligations", harness.Task.AcceptedRetryFeedback?.Message);
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Xunit.Assert.NotNull(decision?.Receipt);
        Xunit.Assert.Equal(EffectReceiptStatus.Applied, decision.Effect?.Status);
        Xunit.Assert.Contains("steward-case=B", decision.Receipt.PrecedentRef);
        Xunit.Assert.Equal(originalBytes, File.ReadAllBytes(rejected.StandardOutputPath!));

        // Exercise the actual authoritative context builder after adjudication cleared the latest pointers.
        var context = Path.Combine(harness.Root, ".orchestrator-context", harness.Goal.Id.Value);
        Directory.CreateDirectory(context);
        File.WriteAllText(Path.Combine(context, "artifact-registry.json"), "{\"artifacts\":[]}");
        var brief = harness.Kernel.BuildTaskBrief(harness.Goal.Id, harness.Task.Id,
            workingDirectory: harness.Root, contextDirectory: context);
        var package = WorkerProfileDispatcher.BuildContextPackage(
            harness.Goal, harness.Task, harness.Root, context, brief);
        var artifact = Xunit.Assert.Single(package.Artifacts.Where(item => item.Identity.Value == "task/rejected-planner/stdout"));
        Xunit.Assert.Equal(ContextDeliveryMode.MandatoryFile, artifact.DeliveryMode);
        var delivered = File.ReadAllBytes(Path.Combine(harness.Root, artifact.MandatoryRelativePath!));
        Xunit.Assert.Equal(originalBytes, delivered);
        Xunit.Assert.True(originalBytes.Length > 4000); // A bounded worker-report projection would discard research here.
        Xunit.Assert.Equal(WorkerContextArtifact.Hash(originalBytes), artifact.ContentHash);
        Xunit.Assert.Contains("MANDATORY READ: identity=task/rejected-planner/stdout", WorkerContextPackageBuilder.Render(package));

        // Simulate the Planner's format-only resubmission; routing itself does not merge or accept requests.
        var corrected = ResearchPlan() + "\n" + AcceptanceRequest("combined", RequiredSelections);
        var correctedRequest = AgentOutputDirectives.ParseHumanInputRequest(corrected, AgentRole.Planner);
        Xunit.Assert.False(correctedRequest.IsMalformed);
        Xunit.Assert.NotNull(correctedRequest.Directive);
        Xunit.Assert.Equal("acceptance-gate", correctedRequest.Directive.EvidenceOwner);
        Xunit.Assert.Equal(RequiredSelections, correctedRequest.Directive.TestNames);
        var originalSelections = OriginalRequests(count).Select(request =>
            AgentOutputDirectives.ParseHumanInputRequest(request, AgentRole.Planner).Directive!)
            .Where(request => request.EvidenceOwner == "acceptance-gate")
            .SelectMany(request => request.TestNames!).Distinct(StringComparer.Ordinal).ToArray();
        Xunit.Assert.Equal(originalSelections, correctedRequest.Directive.TestNames);
        Xunit.Assert.True(PlannerOutputContract.TryValidate(corrected, out _, out diagnostic, Criteria), diagnostic);
        Xunit.Assert.StartsWith(ResearchPlan(), corrected);
        Xunit.Assert.Contains("Operator owns; pending", corrected);
        Xunit.Assert.Single(harness.Task.VerificationHistory);
        Xunit.Assert.DoesNotContain(harness.Goal.Timeline, evt => evt.Kind == ProgressKind.TaskCompleted);
    }

    [Xunit.Theory]
    [Xunit.InlineData(3)]
    [Xunit.InlineData(4)]
    public async Task Multiple_request_repeat_after_store_reopen_escalates_without_another_retry(int count)
    {
        using var harness = new StewardHarness("B");
        var original = ResearchPlan() + "\n" + string.Join("\n", OriginalRequests(count));
        SeedPlanner(harness, "[orchestrator Planner output contract rejection]\n" + MultipleRequests, original);
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());
        host.ServiceTick(harness.Kernel);
        host.ServiceTick(harness.Kernel);
        harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal);
        harness.Coordinator.CompletePersisted([harness.Goal.Id]);
        host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)).Status);
        var retryAt = harness.Task.LatestRetryAt;
        Xunit.Assert.NotNull(retryAt);
        var reopened = CreateHost(harness, new ConductorStewardDeterministicRoute());
        SeedPlanner(harness, "[orchestrator Planner output contract rejection]\n" + MultipleRequests, original,
            retryAt!.Value.AddSeconds(1));

        reopened.ServiceTick(harness.Kernel);
        reopened.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal("steward-owner-question", harness.Goal.CurrentHold?.State);
        Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Single(harness.Goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried));
        Xunit.Assert.Equal(retryAt, harness.Task.LatestRetryAt);
        Xunit.Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
        Xunit.Assert.All(harness.Task.VerificationHistory, verification => Xunit.Assert.False(verification.Succeeded));
        Xunit.Assert.Equal(0, harness.Model.Calls);
        Xunit.Assert.Contains("Owner decision required", File.ReadAllText(harness.ConductPath));
        Xunit.Assert.True(new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db"))
            .HasAppliedRoute(Xunit.Assert.Single(new ConductorStewardTriggerDetector().Detect(harness.Goal))));
    }

    [Xunit.Fact]
    public async Task Known_planner_rejection_routes_without_a_model_round()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, PlanFailure);
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());

        host.ServiceTick(harness.Kernel);
        Xunit.Assert.Equal(0, harness.Model.Calls);
        host.ServiceTick(harness.Kernel);

        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        var payload = Payload(intent);
        Xunit.Assert.Equal(OperatorIntentVerbs.Adjudicate, intent.Verb);
        Xunit.Assert.Equal(harness.Task.Id.Value, intent.TaskId);
        Xunit.Assert.Equal("ContractClarification", payload.Cause);
        Xunit.Assert.Contains("same", payload.Text, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("plan=<non-empty one-sentence summary>", payload.Text);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("attempt")]
    [Xunit.InlineData("recovered-task")]
    public async Task Multiple_request_route_discards_superseded_target(string change)
    {
        using var harness = new StewardHarness("B");
        var stderr = "[orchestrator Planner output contract rejection]\n" + MultipleRequests;
        SeedPlanner(harness, stderr);
        var dispatch = harness.Task.LastDispatch!;
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());
        host.ServiceTick(harness.Kernel);
        if (change == "recovered-task")
            harness.Kernel.RetryTaskWithAuthoritativeFeedback(harness.Goal.Id, harness.Task.Id,
                "Owner already supplied correction", RetryCause.ContractClarification);
        else if (change == "candidate")
        {
            harness.Kernel.RecordDispatchBaseCommit(harness.Goal.Id, harness.Task.Id, new string('b', 40));
            Xunit.Assert.Equal(dispatch.DispatchedAt, harness.Task.LastDispatch!.DispatchedAt);
            Xunit.Assert.Equal(new string('b', 40), harness.Task.LastDispatch.BaseCommit);
            Xunit.Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
        }
        else
        {
            harness.Kernel.RetryTaskWithAuthoritativeFeedback(harness.Goal.Id, harness.Task.Id,
                "Retry before replacement attempt", RetryCause.ContractClarification);
            harness.Kernel.RecordTaskDispatch(harness.Goal.Id, harness.Task.Id,
                dispatch with { DispatchedAt = dispatch.DispatchedAt.AddSeconds(2) });
            var replacement = harness.Task.LastDispatch!;
            harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
                new TaskVerificationRecord("test.exe", harness.Root, 1,
                    "WORKER_RESULT: files: none END_WORKER_RESULT", stderr, replacement.DispatchedAt.AddSeconds(1),
                    WorkerResultPresent: true, DispatchStartedAt: replacement.DispatchedAt,
                    CompletionVerdictRule: "planner-output-contract-rejected"));
            harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Task.Id, WorkTaskStatus.Failed, "failed");
            Xunit.Assert.Equal(dispatch.BaseCommit, replacement.BaseCommit);
            Xunit.Assert.Equal(dispatch.DispatchedAt.AddSeconds(2), replacement.DispatchedAt);
            Xunit.Assert.Equal(replacement.DispatchedAt,
                Xunit.Assert.Single(new ConductorStewardTriggerDetector().Detect(harness.Goal)).OccurredAt);
        }

        host.ServiceTick(harness.Kernel);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Contains("reason=trigger-superseded", File.ReadAllText(harness.ConductPath));
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Example: Malformed PLANNER_EVIDENCE_REQUEST: exactly one evidence request may be emitted per round.")]
    [Xunit.InlineData("Malformed PLANNER_EVIDENCE_REQUEST: exactly one evidence request may be emitted per round.\nunknown additional diagnostic")]
    public async Task Ambiguous_multiple_request_diagnostic_keeps_existing_model_path(string diagnostic)
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, "[orchestrator Planner output contract rejection]\n" + diagnostic);
        harness.Model.Reply("{\"kind\":\"no-action\",\"reason\":\"ambiguous evidence\"}");
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());
        host.ServiceTick(harness.Kernel);
        await host.CurrentRound!;
        host.ServiceTick(harness.Kernel);

        Xunit.Assert.Equal(1, harness.Model.Calls);
        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Equal(WorkTaskStatus.Failed, harness.Task.Status);
    }

    [Xunit.Fact]
    public async Task Wildcard_citation_routes_two_injected_files_without_a_model_round()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, "[orchestrator Planner output contract rejection] " +
            "target citation 'src/Foo*.cs' does not exist and is not marked as a new file; source span [1..12). " +
            "Offending citation: 'src/Foo*.cs'");
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute(new FixedFiles()));

        host.ServiceTick(harness.Kernel);
        host.ServiceTick(harness.Kernel);

        var payload = Payload(Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)));
        Xunit.Assert.Contains("src/Foo*.cs", payload.Text);
        Xunit.Assert.Contains("src/FooOne.cs", payload.Text);
        Xunit.Assert.Contains("src/FooTwo.cs", payload.Text);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Collection_guard_routes_developer_with_required_lane_substring()
    {
        using var harness = new StewardHarness("C");
        harness.Seed("C");
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute(lanes: new FixedLane()),
            new ConductorStewardTriggerDetector((_, _) => "GoalAcceptanceVerifier",
                _ => [harness.TrxPath]));

        host.ServiceTick(harness.Kernel);
        host.ServiceTick(harness.Kernel);

        var intent = Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        var payload = Payload(intent);
        Xunit.Assert.Equal(harness.Task.Id.Value, intent.TaskId);
        Xunit.Assert.Equal("ContractClarification", payload.Cause);
        Xunit.Assert.Contains("ExampleTests", payload.Text);
        Xunit.Assert.Contains("collection 'GoalAcceptanceVerifier'", payload.Text);
        Xunit.Assert.Contains("GoalAcceptanceVerifierTests", payload.Text);
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("A")]
    [Xunit.InlineData("B")]
    public async Task Case_a_and_unrecognized_case_b_still_dispatch_model(string caseLetter)
    {
        using var harness = new StewardHarness(caseLetter);
        if (caseLetter == "A") harness.Seed("A");
        else SeedPlanner(harness, "unrecognized Planner contract reason");
        harness.Model.Reply("{\"kind\":\"no-action\",\"reason\":\"model handled\"}");
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());

        host.ServiceTick(harness.Kernel);
        await host.CurrentRound!;

        Xunit.Assert.Equal(1, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Superseded_deterministic_result_is_discarded_at_harvest()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, PlanFailure);
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute());
        host.ServiceTick(harness.Kernel);
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1, "WORKER_RESULT: failed END_WORKER_RESULT",
                PlanFailure, DateTimeOffset.UtcNow.AddSeconds(2), WorkerResultPresent: true,
                DispatchStartedAt: DateTimeOffset.UtcNow.AddSeconds(1),
                CompletionVerdictRule: "planner-output-contract-rejected"));

        host.ServiceTick(harness.Kernel);

        Xunit.Assert.Empty(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value));
        Xunit.Assert.Contains("reason=trigger-superseded", File.ReadAllText(harness.ConductPath));
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    [Xunit.Fact]
    public async Task Version_change_uses_same_harvest_version_as_model_result()
    {
        using var harness = new StewardHarness("B");
        SeedPlanner(harness, PlanFailure);
        long version = 7;
        var host = CreateHost(harness, new ConductorStewardDeterministicRoute(), version: _ => version);
        host.ServiceTick(harness.Kernel);
        version = 8;

        host.ServiceTick(harness.Kernel);

        var payload = Payload(Xunit.Assert.Single(await harness.Intents.ListForGoalAsync(harness.Goal.Id.Value)));
        Xunit.Assert.Equal(8, payload.ExpectedGoalStateVersion);
        Xunit.Assert.Contains("claimed_goal_version=7 harvest_goal_version=8", File.ReadAllText(harness.ConductPath));
        Xunit.Assert.Equal(0, harness.Model.Calls);
    }

    private static ConductorStewardHost CreateHost(StewardHarness harness,
        ConductorStewardDeterministicRoute route, ConductorStewardTriggerDetector? detector = null,
        Func<GoalId, long?>? version = null) => new(
        new ConductorStewardTriggerStore(Path.Combine(harness.Root, "steward-triggers.db")),
        detector ?? new ConductorStewardTriggerDetector(), harness.Model, harness.Intents,
        new AdjudicationEvidenceResolver(harness.Root), version ?? (_ => 7), _ => harness.Root,
        new GoalLifecycleEventWriter(Path.Combine(harness.Root, "lifecycle")),
        new ConductEventLogWriter(harness.ConductPath), deterministicRoute: route);

    private static void SeedPlanner(StewardHarness harness, string stderr, string? output = null, DateTimeOffset? dispatchedAt = null)
    {
        if (output is not null)
            harness.Kernel.SetGoalRefinedSpec(harness.Goal.Id, new RefinedSpec("Repair the evidence", Criteria,
                VerificationClass.TestVerifiable, [], []) { OperatorOwnedAcceptanceCriteria = [Criteria[1]] });
        var started = dispatchedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        var stdout = output ?? "WORKER_RESULT: files: none END_WORKER_RESULT";
        var stdoutPath = Path.Combine(harness.Root, $"{Guid.NewGuid():N}.out.log");
        File.WriteAllText(stdoutPath, stdout, new UTF8Encoding(false));
        harness.Kernel.RecordTaskDispatch(harness.Goal.Id, harness.Task.Id,
            new TaskDispatchRecord("test-worker", "test.exe", harness.Root, started,
                BaseCommit: StewardHarness.Sha));
        harness.Kernel.RecordTaskVerification(harness.Goal.Id, harness.Task.Id,
            new TaskVerificationRecord("test.exe", harness.Root, 1,
                stdout, stderr, started.AddSeconds(1), StandardOutputPath: stdoutPath,
                WorkerResultPresent: true, DispatchStartedAt: started,
                CompletionVerdictRule: "planner-output-contract-rejected"));
        harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Task.Id, WorkTaskStatus.Failed, "failed");
    }

    private static string AcceptanceRequest(string key, string[] tests) => "PLANNER_EVIDENCE_REQUEST: " +
        JsonSerializer.Serialize(new { criterion_index = 1, evidence_key = key, availability = "post-implementation",
            owner = "acceptance-gate", tests, needed = "Execute all selected candidate tests", reason = "Candidate execution is pending" });

    private static string[] OriginalRequests(int count)
    {
        var requests = count == 3
            ? new[] { AcceptanceRequest("first", RequiredSelections[..5]), AcceptanceRequest("second", RequiredSelections[4..]) }
            : new[] { AcceptanceRequest("first", RequiredSelections[..4]), AcceptanceRequest("second", RequiredSelections[3..7]),
                AcceptanceRequest("third", RequiredSelections[6..]) };
        return [.. requests, "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(new {
            criterion_index = 2, evidence_key = "family-review", availability = "post-implementation", owner = "operator",
            needed = "Review exact candidate with another model family", reason = "Operator owns the pending review" })];
    }

    // Synthetic reproduction of the observed shapes; no claim that these are historical captured payloads.
    private static string ResearchPlan() => """
        ## Source findings
        Current parser rejects multiple evidence directives while retained research remains useful for the next Planner round.
        ## Premise validity
        Source confirms the existing rejection mechanism and the missing deterministic correction. The captured plan retains useful research findings.
        ## Acceptance criteria mapping
        1. disposition=planned; plan=Acceptance executes all required candidate test selections after Developer builds the implementation.
        2. disposition=planned; plan=Operator owns; pending review of the exact candidate with another model family and source-backed resolution of findings.
        ## Target seams and symbols
        Extend `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorStewardDeterministicRoute.cs` at the existing diagnostic correction seam.
        ## Ownership and lifecycle
        Developer implements source and regression cases; Acceptance executes managed tests; Operator provides the independent family review receipt before landing.
        ## External and edge contracts
        Preserve separate ownership obligations and required evidence. Incompatible requests require clarification rather than guessed merges or fabricated execution receipts.
        ## Integration seams
        Route through existing conductor steward ticks and persisted adjudication intents, then authoritative retry feedback and the bounded repeat escalation lifecycle.
        ## Verification commands and classes
        TEST-VERIFIABLE: Acceptance executes `ConductorStewardDeterministicRouteTests` and all requested selections against this candidate; the independent Operator review remains pending.
        ## Risks and stop conditions
        Stop if the original research becomes unavailable, requested test coverage disappears, ambiguous ownership is guessed, or repeated rejection creates another automatic retry.
        """ + "\n" + string.Concat(Enumerable.Repeat("Retained research: authoritative output must survive preview truncation and preserve criterion ownership.\n", 80));

    private static AdjudicateOperatorIntentPayload Payload(OperatorIntentRecord intent) =>
        JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private sealed class FixedFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => ["src/FooOne.cs", "src/FooTwo.cs"];
    }

    private sealed class FixedLane : IConductorStewardLaneSubstringResolver
    {
        public string RequiredSubstring(string worktree, string collection) => "GoalAcceptanceVerifierTests";
    }
}
