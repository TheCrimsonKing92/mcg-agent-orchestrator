using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Dashboard.Api;

public sealed class ProgressiveReviewGlanceTests
{
    private const string IncidentScopeNote = "Scope is violated: work is spread across core/app/domain/reporting/dashboard/test files, while the authoritative trusted scope is limited to docs/test-design-discipline.md";

    [Xunit.Fact]
    public void ParseResult_TypedReasonCode_MapsWithoutProseInference()
    {
        var result = ProgressiveReviewGlanceCoordinator.ParseResult(
            """{"verdict":"fundamental-misdirection","reasonCode":"subsystem","note":"wrong area","evidenceLine":"unmentioned implementation"}""");

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.FundamentalMisdirection, result.Verdict);
        Xunit.Assert.Equal(ProgressiveReviewGlanceReasonCode.Subsystem, result.ReasonCode);
    }

    [Xunit.Fact]
    public void ScopeWordingWithoutLegacyPhrase_InsideScope_Downgrades()
    {
        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(
            Inputs(["docs/test-design-discipline.md"], RepositoryScopeConfidence.Precise),
            new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
                IncidentScopeNote,
                string.Empty));

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.Concern, evaluation.Result.Verdict);
        Xunit.Assert.False(evaluation.Receipt!.LegacyPhraseHintMatched);
        Xunit.Assert.Equal("all-changes-within-trusted-scope", evaluation.Receipt.StructuralComparison);
    }

    [Xunit.Fact]
    public void ScopeWordingWithoutLegacyPhrase_UnknownScope_Downgrades()
    {
        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(
            Inputs([], RepositoryScopeConfidence.Unknown),
            new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
                IncidentScopeNote,
                string.Empty));

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.Concern, evaluation.Result.Verdict);
        Xunit.Assert.Equal("scope-unknown", evaluation.Receipt!.StructuralComparison);
    }

    [Xunit.Fact]
    public void PreciseScope_OutsideChangedFile_PreservesKill()
    {
        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(
            Inputs(["docs/test-design-discipline.md"], RepositoryScopeConfidence.Precise, "src/Other.cs"),
            new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
                IncidentScopeNote,
                string.Empty,
                ReasonCode: ProgressiveReviewGlanceReasonCode.ScopeDeviation));

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.FundamentalMisdirection, evaluation.Result.Verdict);
        Xunit.Assert.False(evaluation.Receipt!.Downgraded);
    }

    [Xunit.Fact]
    public void TypedNonScopeReason_InsideScope_PreservesKill()
    {
        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(
            Inputs(["docs/test-design-discipline.md"], RepositoryScopeConfidence.Precise),
            new ProgressiveReviewGlanceDispatchResult(
                ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
                "Work targets a subsystem the objective never mentions.",
                "Diff implements unrelated scheduling behavior.",
                ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.FundamentalMisdirection, evaluation.Result.Verdict);
        Xunit.Assert.Equal("Subsystem", evaluation.Receipt!.ReasonCode);
    }

    [Xunit.Fact]
    public void GuardReceipt_DashboardWorkSummary_SurfacesExactInputs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-dto-{Guid.NewGuid():N}");
        try
        {
            var now = new DateTimeOffset(2026, 8, 3, 1, 2, 3, TimeSpan.Zero);
            var (kernel, goal, task) = RunningDeveloperRound(now, workingDirectory: root);
            var receipt = new ProgressiveReviewGlanceGuardReceipt(
                "Precise",
                ["docs/test-design-discipline.md"],
                ["docs/test-design-discipline.md"],
                IncidentScopeNote,
                string.Empty,
                "absent",
                false,
                "FundamentalMisdirection",
                "Concern",
                true,
                "all changed files are within the trusted scope",
                "all-changes-within-trusted-scope",
                ["gate=console; record=clarification:gate"],
                OperatorContextTruncated: true,
                CancellationWithheld: true);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, new TestClock(now))
                .AppendProgressiveReviewGlanceGuardReceipt(goal.Id, task.Id, receipt);

            var dto = DashboardResponseMapper.ToGoalWorkSummaryDto(
                kernel,
                goal,
                WorkerProfileCatalog.Default(),
                executionDirectory: root);

            var visible = Xunit.Assert.Single(dto.ProgressiveReviewGlanceGuards!);
            Xunit.Assert.Equal(receipt.Note, visible.Note);
            Xunit.Assert.Equal(receipt.EvidenceLine, visible.EvidenceLine);
            Xunit.Assert.Equal(receipt.ChangedFiles, visible.ChangedFiles);
            Xunit.Assert.True(visible.Downgraded);
            Xunit.Assert.Equal(receipt.GateAnnotations, visible.GateAnnotations);
            Xunit.Assert.True(visible.OperatorContextTruncated);
            Xunit.Assert.True(visible.CancellationWithheld);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FundamentalMisdirection_Downgraded_EmitsCompleteGuardReceipt()
    {
        var now = new DateTimeOffset(2026, 8, 3, 1, 2, 3, TimeSpan.Zero);
        var description = """
            Implement the test discipline update.

            Target files/scopes
            Includes:
            - docs/test-design-discipline.md
            """;
        var (kernel, goal, _) = RunningDeveloperRound(now, description);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            IncidentScopeNote,
            string.Empty));
        var events = new RecordingGlanceEvents();
        var coordinator = NewCoordinator(
            runner,
            events,
            new ProgressiveReviewGlanceOptions(ChangedFileThreshold: 1),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(
                ["docs/test-design-discipline.md"],
                ["docs/test-design-discipline.md"],
                0));

        _ = coordinator.Observe(kernel, [goal]);
        var observed = coordinator.Observe(kernel, [goal]);

        var receipt = Xunit.Assert.Single(events.GuardReceipts);
        Xunit.Assert.Equal("Precise", receipt.ScopeConfidence);
        Xunit.Assert.Equal(IncidentScopeNote, receipt.Note);
        Xunit.Assert.Equal(string.Empty, receipt.EvidenceLine);
        Xunit.Assert.Equal("absent", receipt.ReasonCode);
        Xunit.Assert.Equal(["docs/test-design-discipline.md"], receipt.TrustedScopePaths);
        Xunit.Assert.Equal(["docs/test-design-discipline.md"], receipt.ChangedFiles);
        Xunit.Assert.Equal("FundamentalMisdirection", receipt.OriginalVerdict);
        Xunit.Assert.Equal("Concern", receipt.FinalVerdict);
        Xunit.Assert.True(receipt.Downgraded);
        Xunit.Assert.Equal("all changed files are within the trusted scope", receipt.DowngradeReason);
        Xunit.Assert.Contains(observed.ProgressLines, line =>
            line.Contains("result=guard-evaluated", StringComparison.Ordinal) &&
            line.Contains("downgraded=true", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void FundamentalMisdirection_NotDowngraded_EmitsGuardReceiptAndVisibleEvent()
    {
        var now = new DateTimeOffset(2026, 8, 3, 1, 2, 3, TimeSpan.Zero);
        var description = """
            Implement the test discipline update.

            Target files/scopes
            Includes:
            - docs/test-design-discipline.md
            """;
        var (kernel, goal, _) = RunningDeveloperRound(now, description);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            IncidentScopeNote,
            string.Empty,
            ReasonCode: ProgressiveReviewGlanceReasonCode.ScopeDeviation));
        var events = new RecordingGlanceEvents();
        var coordinator = NewCoordinator(
            runner,
            events,
            new ProgressiveReviewGlanceOptions(ChangedFileThreshold: 1),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(
                ["src/Other.cs"],
                ["docs/test-design-discipline.md"],
                0));

        _ = coordinator.Observe(kernel, [goal]);
        var observed = coordinator.Observe(kernel, [goal]);

        var receipt = Xunit.Assert.Single(events.GuardReceipts);
        Xunit.Assert.False(receipt.Downgraded);
        Xunit.Assert.Equal("FundamentalMisdirection", receipt.FinalVerdict);
        Xunit.Assert.Equal("changes-not-proven-within-trusted-scope", receipt.StructuralComparison);
        Xunit.Assert.Contains(observed.ProgressLines, line =>
            line.Contains("result=guard-evaluated", StringComparison.Ordinal) &&
            line.Contains("downgraded=false", StringComparison.Ordinal));
    }

    private static ProgressiveReviewGlanceInputs Inputs(
        IReadOnlyList<string> trustedScope,
        RepositoryScopeConfidence confidence,
        string changedFile = "docs/test-design-discipline.md",
        IReadOnlyList<ProgressiveReviewOperatorRecord>? operatorRecords = null,
        bool operatorContextTruncated = false) =>
        new(
            "goal-guard",
            "task-guard",
            ProgressiveReviewGlanceTriggerKind.ChangedFiles,
            "changed_files=1",
            "Objective",
            "Brief",
            "Acceptance",
            [],
            [changedFile],
            "diff",
            "transcript",
            trustedScope,
            confidence,
            operatorRecords,
            operatorContextTruncated);

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void GateCoveredFinding_RemovesFault(bool fundamental)
    {
        var verdict = fundamental
            ? ProgressiveReviewGlanceVerdict.FundamentalMisdirection
            : ProgressiveReviewGlanceVerdict.Concern;
        var recordedAt = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var gate = new OperatorGateRecord("hidden-console-spawn", "clarification:10bd7223", recordedAt);
        var inputs = Inputs(
            ["docs/test-design-discipline.md"],
            RepositoryScopeConfidence.Precise,
            operatorRecords:
            [
                new ProgressiveReviewOperatorRecord(
                    "clarification:10bd7223",
                    "answered-clarification",
                    "Question:\nShould suppression ship?\nAnswer:\nGate it until confirmed.",
                    recordedAt,
                    [gate])
            ]);
        var result = new ProgressiveReviewGlanceDispatchResult(
            verdict,
            "hidden-console acquisition is not wired into the max-duration spawn path",
            "missing correlation evidence",
            ReasonCode: ProgressiveReviewGlanceReasonCode.UnmentionedWork,
            Findings:
            [
                new ProgressiveReviewGlanceFinding(
                    "hidden-console-spawn",
                    "hidden-console acquisition is not wired into the max-duration spawn path")
            ]);

        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(inputs, result);

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.OnTrack, evaluation.Result.Verdict);
        Xunit.Assert.Contains(evaluation.Receipt!.GateAnnotations!, item =>
            item.Contains("clarification:10bd7223", StringComparison.Ordinal));
        Xunit.Assert.Equal(
            verdict == ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            evaluation.Receipt.CancellationWithheld);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public void FrozenGoal10bd7223ConcernReceipts_AreGovernedByStructuredGate(int receiptIndex)
    {
        var fixture = LoadGoal10bd7223Fixture();
        var receipt = fixture.ConcernReceipts[receiptIndex];
        var gate = new OperatorGateRecord(
            fixture.DeliverableId,
            fixture.RecordId,
            fixture.RecordedAt);
        var inputs = Inputs(
            ["docs/test-design-discipline.md"],
            RepositoryScopeConfidence.Precise,
            operatorRecords:
            [
                new ProgressiveReviewOperatorRecord(
                    fixture.RecordId,
                    "answered-clarification",
                    $"Question:\n{fixture.ClarificationQuestion}\nAnswer:\n{fixture.ClarificationAnswer}\nObservation:\n{fixture.InconclusiveObservation}",
                    fixture.RecordedAt,
                    [gate])
            ]);
        var result = new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.Concern,
            receipt.Note,
            receipt.EvidenceLine,
            ReasonCode: ProgressiveReviewGlanceReasonCode.UnmentionedWork,
            Findings: [new ProgressiveReviewGlanceFinding(fixture.DeliverableId, receipt.Note)]);

        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(inputs, result);

        Xunit.Assert.Equal("10bd7223", fixture.GoalId);
        Xunit.Assert.Contains("INCONCLUSIVE", fixture.InconclusiveObservation, StringComparison.Ordinal);
        Xunit.Assert.Contains("CreateProcessW", fixture.SliceDiffSummary, StringComparison.Ordinal);
        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.OnTrack, evaluation.Result.Verdict);
        Xunit.Assert.DoesNotContain(receipt.Note, evaluation.Result.Note, StringComparison.Ordinal);
        Xunit.Assert.Contains(evaluation.Receipt!.GateAnnotations!, annotation =>
            annotation.Contains(fixture.RecordId, StringComparison.Ordinal) &&
            annotation.Contains(fixture.DeliverableId, StringComparison.Ordinal));

        // Negative control: identical model prose cannot confer immunity without the typed gate.
        var noGateInputs = inputs with
        {
            OperatorRecords = inputs.EffectiveOperatorRecords
                .Select(record => record with { Gates = [] })
                .ToArray()
        };
        var noGateEvaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(
            noGateInputs,
            result);
        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.Concern, noGateEvaluation.Result.Verdict);
        Xunit.Assert.Contains(receipt.Note, noGateEvaluation.Result.Note, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FrozenGoal10bd7223FundamentalVerdict_DoesNotCancelOrEnqueueSteering()
    {
        var fixture = LoadGoal10bd7223Fixture();
        var now = fixture.RecordedAt.AddMinutes(20);
        var (kernel, goal, task) = RunningDeveloperRound(now.AddMinutes(-10));
        var clarification = kernel.RequestHumanInput(goal.Id, null, fixture.ClarificationQuestion);
        kernel.SubmitHumanInput(
            clarification.Id,
            fixture.ClarificationAnswer,
            [fixture.DeliverableId]);
        kernel.RecordOperatorTaskNote(goal.Id, task.Id, fixture.InconclusiveObservation);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            fixture.ConcernReceipts[0].Note,
            fixture.ConcernReceipts[0].EvidenceLine,
            ReasonCode: ProgressiveReviewGlanceReasonCode.UnmentionedWork,
            Findings:
            [
                new ProgressiveReviewGlanceFinding(
                    fixture.DeliverableId,
                    fixture.ConcernReceipts[0].Note)
            ]));
        var steeringStore = new InMemoryProgressiveReviewSteeringStore();
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-{Guid.NewGuid():N}");
        var coordinator = new ProgressiveReviewGlanceCoordinator(
            runner,
            new RecordingGlanceEvents(),
            new CollaborationItemStore(Path.Combine(root, "items.db")),
            new ProgressiveReviewGlanceOptions(FirstElapsedThreshold: TimeSpan.Zero),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(["handoff.cs"], ["handoff.cs"], 0),
            (_, _) => fixture.SliceDiffSummary,
            _ => "worker remains live",
            steeringStore);

        _ = coordinator.Observe(kernel, [goal]);
        var observed = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.False(observed.MutatedTaskState);
        Xunit.Assert.Empty(steeringStore.Intents);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.Null(task.LastProcess);
    }

    [Xunit.Fact]
    public void GateCoversOneOfTwoFindings_PreservesUncoveredFault()
    {
        var recordedAt = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var inputs = Inputs(
            ["docs/test-design-discipline.md"],
            RepositoryScopeConfidence.Precise,
            operatorRecords:
            [
                new ProgressiveReviewOperatorRecord(
                    "clarification:gate",
                    "answered-clarification",
                    "Gate only the console work.",
                    recordedAt,
                    [new OperatorGateRecord("console", "clarification:gate", recordedAt)])
            ]);
        var result = new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "console missing; database migration targets the wrong subsystem",
            "two gaps",
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem,
            Findings:
            [
                new ProgressiveReviewGlanceFinding("console", "console missing"),
                new ProgressiveReviewGlanceFinding("database", "database migration targets the wrong subsystem")
            ]);

        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(inputs, result);

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.FundamentalMisdirection, evaluation.Result.Verdict);
        Xunit.Assert.DoesNotContain("console missing", evaluation.Result.Note, StringComparison.Ordinal);
        Xunit.Assert.Contains("database migration", evaluation.Result.Note, StringComparison.Ordinal);
        Xunit.Assert.Equal(result.Note, evaluation.Receipt!.Note);
        Xunit.Assert.Equal(result.EvidenceLine, evaluation.Receipt.EvidenceLine);
    }

    [Xunit.Fact]
    public void UncoveredOrAbsentGate_PreservesFundamentalVerdict()
    {
        var recordedAt = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var result = new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "wrong subsystem",
            "database",
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem,
            Findings: [new ProgressiveReviewGlanceFinding("database", "wrong subsystem")]);
        var differentGate = Inputs(
            [],
            RepositoryScopeConfidence.Precise,
            operatorRecords:
            [
                new ProgressiveReviewOperatorRecord(
                    "clarification:gate",
                    "answered-clarification",
                    "Gate console work.",
                    recordedAt,
                    [new OperatorGateRecord("console", "clarification:gate", recordedAt)])
            ]);

        Xunit.Assert.Equal(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            ProgressiveReviewGlanceCoordinator.GuardUnsupportedScopeVerdict(differentGate, result).Verdict);
        Xunit.Assert.Equal(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            ProgressiveReviewGlanceCoordinator.GuardUnsupportedScopeVerdict(
                Inputs([], RepositoryScopeConfidence.Precise),
                result).Verdict);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void UncoveredFindingWithUnrelatedOperatorTextPreservesFault(bool fundamental)
    {
        var verdict = fundamental
            ? ProgressiveReviewGlanceVerdict.FundamentalMisdirection
            : ProgressiveReviewGlanceVerdict.Concern;
        var recordedAt = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var inputs = Inputs(
            [],
            RepositoryScopeConfidence.Precise,
            changedFile: "src/Other.cs",
            operatorRecords:
            [
                new ProgressiveReviewOperatorRecord(
                    "clarification:unrelated",
                    "answered-clarification",
                    "Use the existing serializer.",
                    recordedAt,
                    [])
            ]);
        var result = new ProgressiveReviewGlanceDispatchResult(
            verdict,
            "database migration targets the wrong subsystem",
            "database",
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem);

        var guarded = ProgressiveReviewGlanceCoordinator.GuardUnsupportedScopeVerdict(inputs, result);

        Xunit.Assert.Equal(verdict, guarded.Verdict);
    }

    [Xunit.Fact]
    public void TruncatedOperatorContext_WithholdsContextSensitiveCancellation()
    {
        var result = new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "missing deliverable",
            "context incomplete",
            ReasonCode: ProgressiveReviewGlanceReasonCode.UnmentionedWork);

        var evaluation = ProgressiveReviewGlanceCoordinator.EvaluateUnsupportedScopeVerdict(
            Inputs([], RepositoryScopeConfidence.Precise, operatorContextTruncated: true),
            result);

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.Concern, evaluation.Result.Verdict);
        Xunit.Assert.True(evaluation.Receipt!.OperatorContextTruncated);
        Xunit.Assert.True(evaluation.Receipt.CancellationWithheld);
    }

    [Xunit.Fact]
    public void SatisfiedGate_NoLongerSuppressesFinding()
    {
        var recordedAt = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var satisfiedGate = new OperatorGateRecord(
            "console",
            "clarification:gate",
            recordedAt,
            recordedAt.AddMinutes(20),
            "operator confirmed the hypothesis");
        var inputs = Inputs(
            [],
            RepositoryScopeConfidence.Precise,
            operatorRecords:
            [
                new ProgressiveReviewOperatorRecord(
                    "clarification:gate",
                    "answered-clarification",
                    "Gate console work until confirmed.",
                    recordedAt,
                    [satisfiedGate])
            ]);
        var result = new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "console work missing",
            "console",
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem,
            Findings: [new ProgressiveReviewGlanceFinding("console", "console work missing")]);

        var guarded = ProgressiveReviewGlanceCoordinator.GuardUnsupportedScopeVerdict(inputs, result);

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.FundamentalMisdirection, guarded.Verdict);
    }

    [Xunit.Fact]
    public void InputAssembly_UsesAnsweredClarificationsAndOperatorNotesOnly()
    {
        var now = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now);
        var answered = kernel.RequestHumanInput(goal.Id, null, "Should console suppression ship?");
        var synthetic = kernel.RequestHumanInput(goal.Id, null, "Synthetic parked wait?");
        var riskReview = kernel.RequestHumanInput(
            goal.Id,
            null,
            "May the guarded rollout proceed?",
            HumanWaitKind.RiskReview);
        _ = kernel.RequestHumanInput(goal.Id, null, "Is the correlation complete?");
        kernel.SubmitHumanInput(answered.Id, "Not until the hypothesis is confirmed.", ["hidden-console-spawn"]);
        kernel.SubmitHumanInput(synthetic.Id, "Goal parked: system-authored completion");
        kernel.SubmitHumanInput(riskReview.Id, "Hold the guarded rollout.", ["guarded-rollout"]);
        kernel.RecordTaskNote(goal.Id, task.Id, "worker-authored note must be inert");
        kernel.RecordOperatorTaskNote(goal.Id, task.Id, "INCONCLUSIVE: retain the hypothesis gate.");
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.OnTrack,
            "ok",
            "operator context present"));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now,
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a", "b", "c"], ["a", "b", "c"], 0));

        _ = coordinator.Observe(kernel, [goal]);

        var inputs = Xunit.Assert.Single(runner.Calls);
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords, record => record.Text.Contains("INCONCLUSIVE", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords, record => record.Text.Contains("Not until", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords, record => record.Text.Contains("Hold the guarded rollout", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(inputs.EffectiveOperatorRecords, record => record.Text.Contains("correlation complete", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(inputs.EffectiveOperatorRecords, record => record.Text.Contains("system-authored", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(inputs.EffectiveOperatorRecords, record => record.Text.Contains("worker-authored", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords.SelectMany(record => record.Gates), gate =>
            gate.DeliverableId == "hidden-console-spawn");
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords.SelectMany(record => record.Gates), gate =>
            gate.DeliverableId == "guarded-rollout");
    }

    [Xunit.Fact]
    public void InputAssembly_AppliesExplicitTaskNoteGateSatisfaction()
    {
        var now = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        var (kernel, goal, task) = RunningDeveloperRound(now, clock: clock);
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            "Gate the correlation evidence until the operator confirms it.",
            ["correlation-evidence"]);
        var sourceGate = Xunit.Assert.Single(goal.Timeline
            .Single(evt => evt.Kind == ProgressKind.OperatorTaskNote)
            .OperatorGates!);
        clock.UtcNow = now.AddMinutes(1);
        kernel.MarkOperatorGateSatisfied(
            goal.Id,
            sourceGate.SourceRecordId,
            sourceGate.DeliverableId,
            "operator confirmed three consecutive handoffs");
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.OnTrack,
            "ok",
            "gate expired"));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => clock.UtcNow,
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a", "b", "c"], ["a", "b", "c"], 0));

        _ = coordinator.Observe(kernel, [goal]);

        var gate = Xunit.Assert.Single(Xunit.Assert.Single(runner.Calls).EffectiveOperatorRecords.SelectMany(record => record.Gates));
        Xunit.Assert.False(gate.IsActive);
        Xunit.Assert.Equal("operator confirmed three consecutive handoffs", gate.SatisfactionEvidence);
    }

    [Xunit.Fact]
    public void AmendedAcceptanceSection_OverBudget_IsNotTruncated()
    {
        var now = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Bound the authoritative criteria input",
            ["superseded acceptance requirement"],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            $"CRITERIA CORRECTION: supersedes=\"superseded acceptance requirement\"; correction=\"amended {new string('x', 300)}\"");
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.OnTrack,
            "ok",
            "bounded"));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            options: new ProgressiveReviewGlanceOptions(AcceptanceCharacterLimit: 100),
            utcNow: () => now,
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a", "b", "c"], ["a", "b", "c"], 0));

        _ = coordinator.Observe(kernel, [goal]);

        var section = Xunit.Assert.Single(runner.Calls).AcceptanceSection;
        Xunit.Assert.Contains("Current amended acceptance criteria", section, StringComparison.Ordinal);
        Xunit.Assert.Contains("amended", section, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("superseded acceptance requirement", section, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("...(truncated", section, StringComparison.Ordinal);
        Xunit.Assert.Contains(new string('x', 300), section, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OperatorContextBudgetOmitsOlderTextBeforeNewerAndRetainsStructuredGates()
    {
        var now = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        var (kernel, goal, task) = RunningDeveloperRound(now, clock: clock);
        kernel.RecordOperatorTaskNote(goal.Id, task.Id, "older short note");
        clock.UtcNow = now.AddMinutes(1);
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            "newest governing ruling is intentionally larger than the entire text budget",
            ["hidden-console-spawn"]);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.OnTrack,
            "ok",
            "bounded"));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            options: new ProgressiveReviewGlanceOptions(OperatorContextCharacterLimit: 20),
            utcNow: () => clock.UtcNow,
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a", "b", "c"], ["a", "b", "c"], 0));

        _ = coordinator.Observe(kernel, [goal]);

        var inputs = Xunit.Assert.Single(runner.Calls);
        Xunit.Assert.True(inputs.OperatorContextTruncated);
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords, record =>
            record.Text.Contains("newest governing ruling", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords, record =>
            record.Text.Contains("omitted due to operator-context budget", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(inputs.EffectiveOperatorRecords, record =>
            record.Text.Contains("older short note", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.EffectiveOperatorRecords.SelectMany(record => record.Gates), gate =>
            gate.DeliverableId == "hidden-console-spawn");
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_goal_625_core_first_scope_does_not_steer_from_canned_intake")]
    public void Goal625CoreFirstScopeDoesNotSteerFromCannedIntake()
    {
        var inputs = new ProgressiveReviewGlanceInputs(
            "goal-625",
            "developer",
            ProgressiveReviewGlanceTriggerKind.ChangedFiles,
            "changed_files=3",
            $"Canned intake{Environment.NewLine}{BacklogIntakePlanner.TargetScopeHeadingLine}{Environment.NewLine}- src/Mcg.AgentOrchestrator.App/Cli",
            "Implement the plan-aligned Core-first increment in src/Mcg.AgentOrchestrator.Core.",
            "Pass focused tests.",
            [],
            ["src/Mcg.AgentOrchestrator.Core/Application/ParallelExecutionPlanner.cs"],
            "diff",
            "transcript",
            ["src/Mcg.AgentOrchestrator.Core"],
            RepositoryScopeConfidence.Precise);
        var modelResult = new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Core is outside the scope listed by canned intake target files.",
            "target file differs from App/Cli");

        var guarded = ProgressiveReviewGlanceCoordinator.GuardUnsupportedScopeVerdict(inputs, modelResult);
        var prompt = ProgressiveReviewGlanceCoordinator.BuildPrompt(inputs);

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.Concern, guarded.Verdict);
        Xunit.Assert.Contains("Current task brief", prompt, StringComparison.Ordinal);
        Xunit.Assert.Contains("Core-first increment", prompt, StringComparison.Ordinal);
        Xunit.Assert.Contains("authoritative over generated intake fallback", prompt, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_unknown_scope_cannot_support_scope_deviation_steer")]
    public void UnknownScopeCannotSupportScopeDeviationSteer()
    {
        var inputs = new ProgressiveReviewGlanceInputs(
            "goal-unknown",
            "developer",
            ProgressiveReviewGlanceTriggerKind.ChangedFiles,
            "changed_files=3",
            "No declared paths.",
            "Implement the current task.",
            "Pass focused tests.",
            [],
            ["src/Feature/File.cs"],
            "diff",
            "transcript",
            [],
            RepositoryScopeConfidence.Unknown);
        var modelResult = new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Changed file is outside scope.",
            "scope deviation");

        var guarded = ProgressiveReviewGlanceCoordinator.GuardUnsupportedScopeVerdict(inputs, modelResult);

        Xunit.Assert.Equal(ProgressiveReviewGlanceVerdict.Concern, guarded.Verdict);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_triggers_on_file_count_elapsed_budget_and_concurrent_cap")]
    public void TriggersOnFileCountElapsedBudgetAndConcurrentCap()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now);
        var runner = new ControlledGlanceRunner();
        var events = new RecordingGlanceEvents();
        var coordinator = NewCoordinator(
            runner,
            events,
            utcNow: () => now,
            options: new ProgressiveReviewGlanceOptions(
                FirstElapsedThreshold: TimeSpan.FromMinutes(15),
                ElapsedInterval: TimeSpan.FromMinutes(15)),
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));

        var first = runner.EnqueuePending();
        var result = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.Single(runner.Calls);
        Xunit.Assert.Contains(result.ProgressLines, line => line.Contains("result=started", StringComparison.Ordinal));

        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Single(runner.Calls);

        first.SetResult(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok", "diff aligns", 10, 5));
        Xunit.Assert.True(SpinWait.SpinUntil(
            () =>
            {
                _ = coordinator.Observe(kernel, [goal]);
                return events.Receipts.Count == 1;
            },
            TimeSpan.FromSeconds(2)));
        Xunit.Assert.Single(events.Receipts);
        Xunit.Assert.Equal("ChangedFiles", events.Receipts[0].Trigger);
        Xunit.Assert.Equal("OnTrack", events.Receipts[0].Verdict);
        Xunit.Assert.Equal(15, events.Receipts[0].TotalTokens);

        now += TimeSpan.FromMinutes(16);
        var second = runner.EnqueuePending();
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Equal(2, runner.Calls.Count);
        second.SetResult(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok again", "still aligned", 6, 4));
        Xunit.Assert.True(SpinWait.SpinUntil(
            () =>
            {
                _ = coordinator.Observe(kernel, [goal]);
                return events.Receipts.Count == 2;
            },
            TimeSpan.FromSeconds(2)));

        now += TimeSpan.FromMinutes(16);
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Equal(2, runner.Calls.Count);
        Xunit.Assert.Equal(2, events.Summaries.Last().TotalGlances);
        Xunit.Assert.Equal(25, events.Summaries.Last().TotalTokens);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_suppresses_rounds_with_small_historical_estimate")]
    public void SuppressesRoundsWithSmallHistoricalEstimate()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, _) = RunningDeveloperRound(now, description: "Implement a tiny label change.");
        var runner = new ControlledGlanceRunner();
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now + TimeSpan.FromMinutes(20),
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));
        var stats = new[]
        {
            new TaskDurationStatsRecord(
                AgentRole.Developer,
                TaskComplexity.Simple,
                null,
                null,
                TaskCount: 3,
                AttemptCount: 3,
                FailedAttemptCount: 0,
                MedianLegitimateRuntime: TimeSpan.FromMinutes(5),
                P90LegitimateRuntime: TimeSpan.FromMinutes(6),
                MedianFailureInterventionOverhead: TimeSpan.Zero,
                FailureRate: 0)
        };

        _ = coordinator.Observe(kernel, [goal], stats);

        Xunit.Assert.Empty(runner.Calls);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_dispatch_uses_read_only_profile_selection_and_bounded_inputs")]
    public async Task DispatchUsesReadOnlyProfileSelectionAndBoundedInputs()
    {
        var sparkSelection = SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(WorkerProfileCatalog.Default());
        Xunit.Assert.Equal("codex-spark", sparkSelection.ProfileName);
        Xunit.Assert.Equal("gpt-5.3-codex-spark", sparkSelection.ModelAlias);

        var fallbackSelection = SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(
            new WorkerProfileCatalog([WorkerProfileCatalog.Default().GetRequired("codex-cli")]));
        Xunit.Assert.Equal("codex-cli", fallbackSelection.ProfileName);
        Xunit.Assert.Equal(AgentCatalog.OpenAiSubscriptionModelAlias, fallbackSelection.ModelAlias);

        var command = SubscriptionCliCompleter.SubstitutePlaceholders(
            WorkerProfileCatalog.Default().GetRequired("codex-spark").CommandTemplate,
            "codex-spark",
            "prompt.md",
            "gpt-5.3-codex-spark",
            "low",
            @"C:\work");
        Xunit.Assert.Contains("--sandbox 'read-only'", command, StringComparison.Ordinal);
        Xunit.Assert.Contains("--model 'gpt-5.3-codex-spark'", command, StringComparison.Ordinal);

        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now, description: "Do work\n\nACCEPTANCE\n- Include correction overlay");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "contract",
            ["Use refined acceptance criteria even when task text is stale."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.WaiveAcceptanceCriterion(
            goal.Id,
            "1",
            "requires a conductor-owned external harness",
            "operator:miles");
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["retry feedback is not the criteria correction overlay"]);
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            "CRITERIA CORRECTION: supersedes=\"criterion B\"; correction=\"Correct criterion B before retry.\"");
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            $"CRITERIA CORRECTION: supersedes=\"oversized criterion\"; correction=\"{new string('x', 5000)}\"");
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            "CRITERIA CORRECTION: supersedes=\"extra 01\"; correction=\"Extra correction 01\"");
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            "CRITERIA CORRECTION: supersedes=\"extra 02\"; correction=\"Extra correction 02\"");
        var allFiles = Enumerable.Range(1, 100).Select(index => $"src/File{index:D2}.cs").ToArray();
        var displayFiles = allFiles.Take(3).ToArray();
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok", "bounded", 1, 1));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now,
            options: new ProgressiveReviewGlanceOptions(
                AcceptanceCharacterLimit: 1000,
                CriteriaCorrectionOverlayCharacterLimit: 450,
                CriteriaCorrectionOverlayItemLimit: 3,
                ChangedFilePromptLimit: 5,
                ChangedFileListCharacterLimit: 80,
                DiffCharacterLimit: 30,
                TranscriptCharacterLimit: 20),
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(allFiles, displayFiles, allFiles.Length - displayFiles.Length),
            diffReader: (_, _) => new string('d', 100),
            transcriptReader: _ => new string('t', 100));

        _ = coordinator.Observe(kernel, [goal]);

        var inputs = runner.Calls.Single();
        Xunit.Assert.Contains("Current amended acceptance criteria", inputs.AcceptanceSection, StringComparison.Ordinal);
        Xunit.Assert.Contains("Use refined acceptance criteria", inputs.AcceptanceSection, StringComparison.Ordinal);
        Xunit.Assert.Contains("operator rationale", inputs.AcceptanceSection, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("...(truncated", inputs.AcceptanceSection, StringComparison.Ordinal);
        Xunit.Assert.Contains(inputs.CriteriaCorrectionOverlay, item => item.Contains("Correct criterion B", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.CriteriaCorrectionOverlay, item => item.Contains("status=amended", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain("criterion B", inputs.AcceptanceSection, StringComparison.Ordinal);
        Xunit.Assert.Contains(inputs.CriteriaCorrectionOverlay, item => item.Contains("status=waived", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.CriteriaCorrectionOverlay, item => item.Contains("reason=\"requires a conductor-owned external harness\"", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(inputs.CriteriaCorrectionOverlay, item => item.Contains("correction=\"WAIVED:", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(inputs.CriteriaCorrectionOverlay, item => item.Contains("retry feedback", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(inputs.CriteriaCorrectionOverlay, item => item.Contains("xxxxxxxxxx", StringComparison.Ordinal));
        Xunit.Assert.Contains(inputs.CriteriaCorrectionOverlay, item => item.Contains("more criteria correction", StringComparison.Ordinal));
        Xunit.Assert.Contains("src/File01.cs", inputs.ChangedFiles);
        Xunit.Assert.Contains("src/File05.cs", inputs.ChangedFiles);
        Xunit.Assert.DoesNotContain("src/File06.cs", inputs.ChangedFiles);
        Xunit.Assert.Contains(inputs.ChangedFiles, item => item.Contains("more changed file", StringComparison.Ordinal));
        Xunit.Assert.Equal(new string('d', 100), inputs.DiffExcerpt);
        Xunit.Assert.True(inputs.TranscriptTail.Length < 90);

        var lightDelivery = await CaptureGlancePromptDeliveryAsync(WorkerProfileCatalog.Default(), inputs);
        var fallbackDelivery = await CaptureGlancePromptDeliveryAsync(
            new WorkerProfileCatalog([WorkerProfileCatalog.Default().GetRequired("codex-cli")]),
            inputs);

        Xunit.Assert.Equal("codex-spark", lightDelivery.ProfileName);
        Xunit.Assert.Equal("codex-cli", fallbackDelivery.ProfileName);
        foreach (var delivery in new[] { lightDelivery, fallbackDelivery })
        {
            Xunit.Assert.False(string.IsNullOrWhiteSpace(delivery.StandardInput));
            Xunit.Assert.Contains("Progressive review goal objective", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("Use refined acceptance criteria", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("operator rationale", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("Correct criterion B", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("status=amended", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("supersedes=\"criterion B\"", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("retry feedback", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("xxxxxxxxxx", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("src/File01.cs", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("src/File06.cs", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("more changed file", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains(new string('d', 100), delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("tttttttttttttttttttt", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("ttttttttttttttttttttt", delivery.StandardInput!, StringComparison.Ordinal);
            Xunit.Assert.Contains("--sandbox 'read-only'", delivery.Command, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_default_transcript_reader_tolerates_live_stdout_append_handle")]
    public void DefaultTranscriptReaderToleratesLiveStdoutAppendHandle()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var root = CreateTempDirectory("mcg-glance-live-log");
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "exit.txt");
        File.WriteAllText(stdout, "first line\nlive worker wrote this\n");
        File.WriteAllText(stderr, string.Empty);
        try
        {
            var (kernel, goal, task) = RunningDeveloperRound(now, workingDirectory: root);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, CreateProcessRecord(root, stdout, stderr, exit, now));
            var runner = new ControlledGlanceRunner();
            var coordinator = NewCoordinator(
                runner,
                new RecordingGlanceEvents(),
                utcNow: () => now,
                liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
                useDefaultTranscriptReader: true);

            using var liveWriter = new FileStream(stdout, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            liveWriter.Seek(0, SeekOrigin.End);
            liveWriter.Write(Encoding.UTF8.GetBytes("append still open\n"));
            liveWriter.Flush();

            var result = coordinator.Observe(kernel, [goal]);

            var inputs = runner.Calls.Single();
            Xunit.Assert.Contains("result=started", result.ProgressLines.Single(), StringComparison.Ordinal);
            Xunit.Assert.Contains("live worker wrote this", inputs.TranscriptTail, StringComparison.Ordinal);
            Xunit.Assert.Contains("append still open", inputs.TranscriptTail, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain(result.ProgressLines, line => line.Contains("advisory-error", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_log_tail_is_byte_bounded_and_line_aligned")]
    public void LogTailIsByteBoundedAndLineAligned()
    {
        var root = CreateTempDirectory("mcg-glance-tail");
        var large = Path.Combine(root, "large.log");
        var lineAligned = Path.Combine(root, "aligned.log");
        try
        {
            File.WriteAllText(large, string.Concat(Enumerable.Repeat(new string('a', 79) + "\n", 2000)));

            var tail = ProgressiveReviewGlanceCoordinator.ReadLogTail(large, 65536);

            Xunit.Assert.True(Encoding.UTF8.GetByteCount(tail) <= 65536);
            Xunit.Assert.True(tail.Length < File.ReadAllText(large).Length);

            File.WriteAllText(lineAligned, new string('x', 80) + "\ncomplete-one\ncomplete-two\n");

            var alignedTail = ProgressiveReviewGlanceCoordinator.ReadLogTail(lineAligned, 32);

            Xunit.Assert.StartsWith("complete-one", alignedTail, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("x", alignedTail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_transcript_read_failures_remain_advisory_errors")]
    public void TranscriptReadFailuresRemainAdvisoryErrors()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var root = CreateTempDirectory("mcg-glance-locked-log");
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "exit.txt");
        File.WriteAllText(stdout, "locked stdout\n");
        File.WriteAllText(stderr, string.Empty);
        try
        {
            var (kernel, goal, task) = RunningDeveloperRound(now, workingDirectory: root);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, CreateProcessRecord(root, stdout, stderr, exit, now));
            var runner = new ControlledGlanceRunner();
            var coordinator = NewCoordinator(
                runner,
                new RecordingGlanceEvents(),
                utcNow: () => now,
                liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
                useDefaultTranscriptReader: true);

            using var lockedStdout = new FileStream(stdout, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = coordinator.Observe(kernel, [goal]);

            Xunit.Assert.Empty(runner.Calls);
            Xunit.Assert.Contains(result.ProgressLines, line => line.Contains("GLANCE result=advisory-error", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_concern_surfaces_only_after_same_round_failure")]
    public void ConcernSurfacesOnlyAfterSameRoundFailure()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.Concern,
            "Acceptance criterion B appears untouched.",
            "diff lacks criterion B",
            5,
            5,
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now,
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));

        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Empty(task.CriterionRetryFeedback);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord("verify", "C:\\work", 1, "failed", "", now.AddMinutes(1)));
        var result = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.True(result.MutatedTaskState);
        Xunit.Assert.Empty(task.CriterionRetryFeedback);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Progressive review glance concern", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_concern_survives_developer_success_until_reviewer_failure")]
    public void ConcernSurvivesDeveloperSuccessUntilReviewerFailure()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        var (kernel, goal, developer, reviewer) = RunningDeveloperAndReviewerRound(now, clock);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.Concern,
            "Acceptance criterion B appears untouched.",
            "diff lacks criterion B",
            5,
            5,
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));
        var coordinator = NewCoordinator(
            runner,
            new RecordingGlanceEvents(),
            utcNow: () => now,
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));

        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord("verify", "C:\\work", 0, "passed", "", now.AddMinutes(1)));
        _ = coordinator.Observe(kernel, [goal]);
        Xunit.Assert.Empty(developer.CriterionRetryFeedback);

        kernel.RecordTaskDispatch(
            goal.Id,
            reviewer.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "review",
                "C:\\work",
                now.AddMinutes(2),
                BaseCommit: "base"));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            reviewer.Id,
            new TaskVerificationRecord("review", "C:\\work", 1, "needs-work", "", now.AddMinutes(3)));
        var result = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.True(result.MutatedTaskState);
        Xunit.Assert.Empty(developer.CriterionRetryFeedback);
        Xunit.Assert.Equal(0, developer.CriterionRetryCount);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Progressive review glance concern", StringComparison.Ordinal));

        clock.UtcNow = now.AddMinutes(4);
        kernel.RetryTask(goal.Id, developer.Id, "Reviewer auto-review-retry found criterion B still missing.");
        _ = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.Equal(1, goal.Timeline.Count(evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Progressive review glance concern", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_timeout_records_invalid_receipt_and_releases_cap")]
    public void TimeoutRecordsInvalidReceiptAndReleasesCap()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, _) = RunningDeveloperRound(now);
        var runner = new ControlledGlanceRunner();
        _ = runner.EnqueuePending();
        var events = new RecordingGlanceEvents();
        var coordinator = NewCoordinator(
            runner,
            events,
            utcNow: () => now,
            options: new ProgressiveReviewGlanceOptions(
                PerRoundBudget: 1,
                DispatchTimeout: TimeSpan.FromMilliseconds(1)),
            liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));

        _ = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.True(SpinWait.SpinUntil(
            () =>
            {
                _ = coordinator.Observe(kernel, [goal]);
                return events.Receipts.Count == 1;
            },
            TimeSpan.FromSeconds(2)));
        Xunit.Assert.Equal("Invalid", events.Receipts.Single().Verdict);
        Xunit.Assert.Contains("timed out", events.Receipts.Single().Note, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_default_diff_includes_untracked_file_content")]
    public void DefaultDiffIncludesUntrackedFileContent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-diff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "NewFile.cs"), "public sealed class NewFile {}\n");
        var saved = ProgressiveReviewGlanceCoordinator.RunGit;
        try
        {
            ProgressiveReviewGlanceCoordinator.RunGit = (_, args) =>
            {
                if (args.SequenceEqual(["ls-files", "--others", "--exclude-standard"]))
                {
                    return new GitCli.GitResult(0, "src/NewFile.cs\n", string.Empty);
                }

                return new GitCli.GitResult(0, string.Empty, string.Empty);
            };

            var diff = ProgressiveReviewGlanceCoordinator.ReadDiff(root, "base");

            Xunit.Assert.Contains("diff --git a/src/NewFile.cs b/src/NewFile.cs", diff, StringComparison.Ordinal);
            Xunit.Assert.Contains("+++ b/src/NewFile.cs", diff, StringComparison.Ordinal);
            Xunit.Assert.Contains("+public sealed class NewFile {}", diff, StringComparison.Ordinal);
        }
        finally
        {
            ProgressiveReviewGlanceCoordinator.RunGit = saved;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_misdirection_raises_one_deduped_attention_item")]
    public void MisdirectionRaisesOneDedupedAttentionItem()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, _) = RunningDeveloperRound(now);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-{Guid.NewGuid():N}");
        var store = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Task asks for forbidden session resume work.",
            "diff adds session resume primitive",
            5,
            5,
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Task still asks for forbidden session resume work.",
            "diff adds second session resume primitive",
            5,
            5,
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));
        var coordinator = new ProgressiveReviewGlanceCoordinator(
            runner,
            new RecordingGlanceEvents(),
            store,
            new ProgressiveReviewGlanceOptions(FirstElapsedThreshold: TimeSpan.Zero),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
            (_, _) => "diff",
            _ => "transcript");

        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);

        var items = store.ListAsync(goal.Id.Value).GetAwaiter().GetResult();
        Xunit.Assert.Single(items);
        Xunit.Assert.Equal(CollaborationItemType.Decision, items.Single().Type);
        Xunit.Assert.Contains("cancel plus resume-with-guidance", items.Single().Body, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_misdirection_enqueues_steer_intent_without_attention_when_store_is_available")]
    public void MisdirectionEnqueuesSteerIntentWhenStoreIsAvailable()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, _) = RunningDeveloperRound(now);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-{Guid.NewGuid():N}");
        var collaborationStore = new CollaborationItemStore(Path.Combine(root, "items.db"));
        var steeringStore = new InMemoryProgressiveReviewSteeringStore();
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Correct toward the scoped implementation.",
            "diff edits the forbidden surface",
            5,
            5,
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));
        var coordinator = new ProgressiveReviewGlanceCoordinator(
            runner,
            new RecordingGlanceEvents(),
            collaborationStore,
            new ProgressiveReviewGlanceOptions(FirstElapsedThreshold: TimeSpan.Zero),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
            (_, _) => "diff",
            _ => "transcript",
            steeringStore);

        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);

        var intent = Assert.Single(steeringStore.Intents);
        Assert.Equal(goal.Id.Value, intent.GoalId);
        Assert.Equal("diff edits the forbidden surface", intent.MisdirectionEvidence);
        Assert.Contains("authoritative over remembered session context", intent.GuidanceText, StringComparison.Ordinal);
        Assert.Contains("diff", intent.GuidanceText, StringComparison.Ordinal);
        Assert.Empty(collaborationStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_steer_inputs_hash_includes_session_context_not_only_glance_inputs")]
    public void SteerInputsHashIncludesSessionContextNotOnlyGlanceInputs()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var first = CaptureSteerIntentForSession(now, "session-one");
        var second = CaptureSteerIntentForSession(now, "session-two");

        Assert.Equal(first.GlanceInputHash, second.GlanceInputHash);
        Assert.NotEqual(first.Intent.InputsHash, first.GlanceInputHash);
        Assert.NotEqual(first.Intent.InputsHash, second.Intent.InputsHash);
        Assert.Equal($"glance-{first.Intent.InputsHash}", first.Intent.Id);
        Assert.Equal(first.Intent.Id, first.Intent.TriggerGlanceId);
        Assert.NotEqual(first.Intent.Id, second.Intent.Id);
    }

    [Xunit.Fact(DisplayName = "ProgressiveReviewGlance_receipt_and_attention_failures_are_advisory_only")]
    public void ReceiptAndAttentionFailuresAreAdvisoryOnly()
    {
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var (kernel, goal, task) = RunningDeveloperRound(now);
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Task asks for forbidden session resume work.",
            "diff adds session resume primitive",
            5,
            5,
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));
        var coordinator = new ProgressiveReviewGlanceCoordinator(
            runner,
            new RecordingGlanceEvents { ThrowOnProgressiveWrites = true },
            new ThrowingCollaborationItemStore(),
            new ProgressiveReviewGlanceOptions(),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
            (_, _) => "diff",
            _ => "transcript");

        _ = coordinator.Observe(kernel, [goal]);
        var result = coordinator.Observe(kernel, [goal]);

        Xunit.Assert.False(result.MutatedTaskState);
        Xunit.Assert.Equal(WorkTaskStatus.Running, task.Status);
        Xunit.Assert.Contains(result.ProgressLines, line => line.Contains("receipt-write-failed", StringComparison.Ordinal));
        Xunit.Assert.Contains(result.ProgressLines, line => line.Contains("summary-write-failed", StringComparison.Ordinal));
        Xunit.Assert.Contains(result.ProgressLines, line => line.Contains("attention-write-failed", StringComparison.Ordinal));
    }

    private static ProgressiveReviewGlanceCoordinator NewCoordinator(
        ControlledGlanceRunner runner,
        RecordingGlanceEvents events,
        ProgressiveReviewGlanceOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string?, string?, DispatchLiveChangeSnapshot>? liveChanges = null,
        Func<string, string?, string>? diffReader = null,
        Func<TaskProcessRecord?, string>? transcriptReader = null,
        bool useDefaultTranscriptReader = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-{Guid.NewGuid():N}");
        return new ProgressiveReviewGlanceCoordinator(
            runner,
            events,
            new CollaborationItemStore(Path.Combine(root, "items.db")),
            options,
            utcNow,
            liveChanges,
            diffReader ?? ((_, _) => "diff"),
            useDefaultTranscriptReader ? transcriptReader : transcriptReader ?? (_ => "transcript"));
    }

    private static Goal10bd7223Fixture LoadGoal10bd7223Fixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "Fixtures",
            "progressive-review-10bd7223.json");
        return JsonSerializer.Deserialize<Goal10bd7223Fixture>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) RunningDeveloperRound(
        DateTimeOffset dispatchedAt,
        string description = "Implement feature.\n\nACCEPTANCE\n- Pass focused tests",
        IClock? clock = null,
        string workingDirectory = @"C:\work",
        string? providerSessionId = null,
        string? worktreeHeadSha = null)
    {
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(new TaskId("developer-task-0001"), description, AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId("goal-progressive-review-0001"), "Progressive review goal objective", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                workingDirectory,
                dispatchedAt,
                BaseCommit: "base",
                ProviderSessionId: providerSessionId,
                WorktreeHeadSha: worktreeHeadSha));
        return (kernel, goal, task);
    }

    private static (ProgressiveReviewSteerIntent Intent, string GlanceInputHash) CaptureSteerIntentForSession(
        DateTimeOffset now,
        string sessionId)
    {
        var (kernel, goal, _) = RunningDeveloperRound(now, providerSessionId: sessionId);
        var root = Path.Combine(Path.GetTempPath(), $"mcg-glance-{Guid.NewGuid():N}");
        var steeringStore = new InMemoryProgressiveReviewSteeringStore();
        var runner = new ControlledGlanceRunner();
        runner.EnqueueCompleted(new ProgressiveReviewGlanceDispatchResult(
            ProgressiveReviewGlanceVerdict.FundamentalMisdirection,
            "Correct toward the scoped implementation.",
            "diff edits the forbidden surface",
            5,
            5,
            ReasonCode: ProgressiveReviewGlanceReasonCode.Subsystem));
        var events = new RecordingGlanceEvents();
        var coordinator = new ProgressiveReviewGlanceCoordinator(
            runner,
            events,
            new CollaborationItemStore(Path.Combine(root, "items.db")),
            new ProgressiveReviewGlanceOptions(FirstElapsedThreshold: TimeSpan.Zero),
            () => now,
            (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0),
            (_, _) => "diff",
            _ => "transcript",
            steeringStore);

        _ = coordinator.Observe(kernel, [goal]);
        _ = coordinator.Observe(kernel, [goal]);

        return (Assert.Single(steeringStore.Intents), Assert.Single(events.Receipts).InputsHash);
    }

    private static TaskProcessRecord CreateProcessRecord(
        string root,
        string stdout,
        string stderr,
        string exit,
        DateTimeOffset startedAt) =>
        new(12345, "codex exec", root, stdout, stderr, exit, startedAt, null, null);

    private static string CreateTempDirectory(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, TaskSpec Reviewer) RunningDeveloperAndReviewerRound(
        DateTimeOffset dispatchedAt,
        IClock clock)
    {
        var kernel = new AgentOrchestratorKernel(clock);
        var developer = new TaskSpec(new TaskId("developer-task-0001"), "Implement feature.\n\nACCEPTANCE\n- Pass focused tests", AgentRole.Developer);
        var reviewer = new TaskSpec(new TaskId("reviewer-task-0001"), "Review implementation.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal(new GoalId("goal-progressive-review-0001"), "Progressive review goal objective", [developer, reviewer]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord(
                "codex-cli",
                "codex exec",
                @"C:\work",
                dispatchedAt,
                BaseCommit: "base"));
        return (kernel, goal, developer, reviewer);
    }

    private static async Task<CapturedPromptDelivery> CaptureGlancePromptDeliveryAsync(
        WorkerProfileCatalog profiles,
        ProgressiveReviewGlanceInputs inputs)
    {
        var selection = SubscriptionCliProgressiveReviewGlanceRunner.SelectProfile(profiles);
        string? capturedCommand = null;
        string? capturedStandardInput = null;
        var completer = new SubscriptionCliCompleter(
            profiles.GetRequired(selection.ProfileName).CommandTemplate,
            selection.ProfileName,
            selection.ModelAlias,
            AgentCatalog.RoutineSubscriptionReasoningEffort,
            (command, _, standardInput, _) =>
            {
                capturedCommand = command;
                capturedStandardInput = standardInput;
                return Task.FromResult("""{"verdict":"on-track","note":"ok","evidenceLine":"prompt delivered"}""");
            });

        var prompt = ProgressiveReviewGlanceCoordinator.BuildPrompt(inputs);
        _ = await completer.CompleteAsync(prompt, "progressive-review-glance.md", default);

        return new CapturedPromptDelivery(selection.ProfileName, capturedCommand!, capturedStandardInput);
    }

    private sealed record CapturedPromptDelivery(
        string ProfileName,
        string Command,
        string? StandardInput);

    private sealed record Goal10bd7223Fixture(
        string GoalId,
        string RecordId,
        string DeliverableId,
        DateTimeOffset RecordedAt,
        string ClarificationQuestion,
        string ClarificationAnswer,
        string InconclusiveObservation,
        string SliceDiffSummary,
        IReadOnlyList<Goal10bd7223ConcernReceipt> ConcernReceipts);

    private sealed record Goal10bd7223ConcernReceipt(
        DateTimeOffset RecordedAt,
        string Note,
        string EvidenceLine);

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class ControlledGlanceRunner : IProgressiveReviewGlanceRunner
    {
        private readonly Queue<Task<ProgressiveReviewGlanceDispatchResult>> _responses = [];

        public List<ProgressiveReviewGlanceInputs> Calls { get; } = [];

        public TaskCompletionSource<ProgressiveReviewGlanceDispatchResult> EnqueuePending()
        {
            var source = new TaskCompletionSource<ProgressiveReviewGlanceDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _responses.Enqueue(source.Task);
            return source;
        }

        public void EnqueueCompleted(ProgressiveReviewGlanceDispatchResult result) =>
            _responses.Enqueue(Task.FromResult(result));

        public Task<ProgressiveReviewGlanceDispatchResult> RunAsync(
            ProgressiveReviewGlanceInputs inputs,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(inputs);
            return _responses.Count == 0
                ? Task.FromResult(new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok", "ok", 1, 1))
                : _responses.Dequeue();
        }
    }

    private sealed class RecordingGlanceEvents : IGoalLifecycleEventWriter
    {
        public List<Receipt> Receipts { get; } = [];
        public List<Summary> Summaries { get; } = [];
        public List<ProgressiveReviewGlanceGuardReceipt> GuardReceipts { get; } = [];
        public bool ThrowOnProgressiveWrites { get; init; }

        public void AppendTimelineEvent(ProgressEvent progressEvent) { }
        public void AppendGoalCreated(GoalId goalId, string objective) { }
        public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
        public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> staleTopicKeys, string recoveryCommand) { }
        public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
        public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }
        public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) { }
        public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string capturedAcceptanceCriteriaHash) { }
        public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }
        public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) { }
        public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) { }
        public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }
        public void AppendCleanedUp(GoalId goalId) { }

        public void AppendProgressiveReviewGlanceReceipt(
            GoalId goalId,
            TaskId taskId,
            string trigger,
            string inputsHash,
            string verdict,
            string note,
            int inputTokens,
            int outputTokens,
            int totalTokens,
            TimeSpan wallTime,
            string? model,
            string? profile)
        {
            if (ThrowOnProgressiveWrites)
            {
                throw new InvalidOperationException("receipt sink unavailable");
            }

            Receipts.Add(new Receipt(trigger, inputsHash, verdict, note, inputTokens, outputTokens, totalTokens, wallTime, model, profile));
        }

        public void AppendProgressiveReviewGlanceGuardReceipt(
            GoalId goalId,
            TaskId taskId,
            ProgressiveReviewGlanceGuardReceipt receipt)
        {
            if (ThrowOnProgressiveWrites)
                throw new InvalidOperationException("guard receipt sink unavailable");
            GuardReceipts.Add(receipt);
        }

        public void AppendProgressiveReviewGlanceSummary(
            GoalId goalId,
            int totalGlances,
            int onTrack,
            int concern,
            int fundamentalMisdirection,
            int invalid,
            int totalTokens)
        {
            if (ThrowOnProgressiveWrites)
            {
                throw new InvalidOperationException("summary sink unavailable");
            }

            Summaries.Add(new Summary(totalGlances, onTrack, concern, fundamentalMisdirection, invalid, totalTokens));
        }
    }

    private sealed class ThrowingCollaborationItemStore : ICollaborationItemStore
    {
        public Task<CollaborationItem> RaiseAsync(
            CollaborationItemType type,
            string? goalId,
            string subject,
            string body,
            string? correlationKey = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("attention store unavailable");

        public Task<CollaborationItem> RaiseWithActionsAsync(
            CollaborationItemType type,
            string? goalId,
            string subject,
            string body,
            string correlationKey,
            IReadOnlyList<CollaborationActionBinding> actions,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryResolveAsync(
            string correlationKey,
            string resolution,
            CancellationToken cancellationToken = default,
            int? briefVersion = null) =>
            throw new NotSupportedException();

        public Task<int> ResolveOpenForGoalAsync(string goalId, string resolution, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryMarkDeliveredAsync(string correlationKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CollaborationItem>> GetAttentionQueueAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CollaborationItem>> ListAsync(string? goalId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CollaborationItem>> ListForGoalIdsAsync(IEnumerable<string> goalIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CollaborationBoundAction>> ListActionsAsync(string correlationKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateRenderedContentHashAsync(IEnumerable<string> correlationKeys, string renderedContentHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CollaborationActionApplyResult> TryClaimActionAsync(
            string correlationKey,
            int actionIndex,
            string actorId,
            string interactionId,
            long? currentGoalStateVersion,
            DateTimeOffset decidedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CollaborationDecisionAuditEntry> RecordRejectedDecisionAsync(
            string correlationKey,
            int? actionIndex,
            string actorId,
            string interactionId,
            string reason,
            DateTimeOffset decidedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CollaborationDecisionAuditEntry>> ListDecisionAuditAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DecisionRequest> RaiseDecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DecisionState?> GetDecisionStateAsync(string requestId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DecisionRequest>> ListDecisionRequestsAsync(string? goalId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<NotificationDelivery> RecordNotificationDeliveryAsync(NotificationDelivery delivery, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NotificationDelivery>> ListNotificationDeliveriesAsync(string requestId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DecisionReceipt> RecordDecisionAsync(
            string requestId,
            string actorId,
            string channel,
            AuthorizationTier authenticationAssurance,
            long? expectedGoalStateVersion,
            DecisionResponse response,
            DateTimeOffset recordedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DecisionReceipt> RecordExpiredDefaultDispositionAsync(string requestId, DateTimeOffset expiredAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DecisionEffectApplyResult> TryApplyDecisionEffectAsync(
            string requestId,
            string decisionReceiptId,
            DecisionActionRef actionRef,
            long? currentGoalStateVersion,
            string result,
            DateTimeOffset appliedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record Receipt(
        string Trigger,
        string InputsHash,
        string Verdict,
        string Note,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        TimeSpan WallTime,
        string? Model,
        string? Profile);

    private sealed record Summary(
        int TotalGlances,
        int OnTrack,
        int Concern,
        int FundamentalMisdirection,
        int Invalid,
        int TotalTokens);
}
