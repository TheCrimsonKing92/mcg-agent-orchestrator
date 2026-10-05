using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its kernel and separate temporary SQLite stores.
public sealed class OperatorIntentAnswerFrozenFactRulingTests
{
    private const string Ruling = """
        Frozen-fact ruling v1
        Frozen classes: ConductorBatchLoopTestsSelfHandoffDrainCap, RepositoryChangeClassifierDispatchHandlingPathsTests
        Amended fact: ConductorBatchLoopTestsSelfHandoffDrainCap.DispatchHandlingLandingKeepsFullDrainAtCap in tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsSelfHandoffDrainCap.cs: In the one RunScenario path literal for BackgroundDispatchRunner.cs, change only the project segment src/Mcg.AgentOrchestrator.Infrastructure/ to src/Mcg.AgentOrchestrator.Execution/ (the Processes/BackgroundDispatchRunner.cs remainder and the true argument stay).
        Amended fact: RepositoryChangeClassifierDispatchHandlingPathsTests.DispatchAndResultHandlingChangesKeepTheFullDrain in tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierDispatchHandlingPathsTests.cs: In the backslash InlineData literal for BackgroundDispatchContracts.cs (line 14), change only the project segment src\Mcg.AgentOrchestrator.Infrastructure\ to src\Mcg.AgentOrchestrator.Execution\, keeping backslash separators and the Processes\BackgroundDispatchContracts.cs remainder. This adds to ruling 11's forward-slash InlineData rewrites in the same fact; it does not replace them.
        Basis: Both facts feed an old Infrastructure/Processes path into TouchesDispatchResultHandling (directly, or through ConductorBatchLoop.SelfRelaunchDrainCap) and assert it is dispatch handling. That is the old per-folder row behavior this goal deliberately removes, and the Execution twin of each path (Processes/BackgroundDispatch* rows) is classified the same way. The goal brief's ruling 11 already allows project-segment-only rewrites of the same kind in the sibling InlineData lines. The two literals were left out of ruling 11: one is a backslash path and one sits in a different test class. Both are unchanged since 09c4911ed.
        Unmodified: In DispatchHandlingLandingKeepsFullDrainAtCap every assertion stays byte-identical: Detached==0, Cancelled==0, no LOOP_RELAUNCH_DETACH, reason=dispatch-handling-changed, TerminalReceiptBeforeHandoff and HandoffStarted. In ConductorBatchLoopTestsSelfHandoffDrainCap, UnprovenDispatchKeepsFullDrainAtCap and DrainCapAcceptsOnlyPositiveIntegerMinutes stay untouched. In RepositoryChangeClassifierDispatchHandlingPathsTests, the Assert.True and Assert.False bodies, every other InlineData line beyond ruling 11's listed rewrites, and UnrelatedChangesAllowTheCappedDrain stay byte-identical, including the negative BackgroundDispatchNotes.md case per ruling 11. The ruling 11 diff check still applies to the other files it names.
        Diff check: Run git diff main -U0 -- tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsSelfHandoffDrainCap.cs. It must show exactly one changed line, the RunScenario path literal, differing only by Infrastructure becoming Execution in the project segment. Run git diff main -U0 -- tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierDispatchHandlingPathsTests.cs. Every changed line must differ from its original only by that project-segment rewrite, either ruling 11's forward-slash InlineData lines or the one backslash line 14. Any added or removed line, or any other changed character, fails the check.
        Evidence: tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorBatchLoopTestsSelfHandoffDrainCap.cs:59, tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierDispatchHandlingPathsTests.cs:14, src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier.DispatchHandlingPaths.cs:9, src/Mcg.AgentOrchestrator.Core/Application/RepositoryChangeClassifier.DispatchHandlingPaths.cs:15
        """;

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Malformed_ruling_is_rejected_and_request_stays_unanswered(bool surroundingWhitespace)
    {
        using var fixture = new AnswerFixture();
        var text = string.Join("\n", Ruling.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.StartsWith("Diff check:", StringComparison.Ordinal)));
        if (surroundingWhitespace) text = " \r\n\r\n" + text.ReplaceLineEndings("\r\n") + "\r\n ";
        var intent = await fixture.Enqueue(text);

        Xunit.Assert.False(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        var rejected = await fixture.Intents.GetAsync(intent.Id);
        Xunit.Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Xunit.Assert.Contains("frozen-fact-ruling-malformed", rejected.Outcome, StringComparison.Ordinal);
        Xunit.Assert.False(fixture.Request.IsCompleted);
        Xunit.Assert.Null(fixture.Request.Answer);
        Xunit.Assert.Contains(fixture.Request, fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, fixture.Task.Status);
        Xunit.Assert.Null(await fixture.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"));
    }

    [Xunit.Fact]
    public async Task Valid_operator_ruling_applies_and_reaches_reviewer_brief_section()
    {
        using var fixture = new AnswerFixture();
        var intent = await fixture.Enqueue(Ruling);
        Xunit.Assert.True(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Xunit.Assert.True(fixture.Request.IsCompleted);
        Xunit.Assert.Equal(Ruling, fixture.Request.Answer);

        var section = typeof(FrozenFactRuling).Assembly.GetType(
            "Mcg.AgentOrchestrator.Core.FrozenFactRulingBriefSection", throwOnError: true)!;
        var render = section.GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Static)!;
        var lines = (IReadOnlyList<string>)render.Invoke(null,
            [fixture.Kernel.HumanInputRequests, fixture.Goal.Id, AgentRole.Reviewer])!;
        var instruction = (string)section.GetField("ReviewerInstruction",
            BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Xunit.Assert.Contains("Frozen classes: ConductorBatchLoopTestsSelfHandoffDrainCap, RepositoryChangeClassifierDispatchHandlingPathsTests", lines);
        Xunit.Assert.Contains(instruction, lines);
    }

    [Xunit.Theory]
    [Xunit.InlineData("  Use the existing path.\r\n")]
    [Xunit.InlineData("Ordinary prose\nFrozen-fact ruling v1")]
    [Xunit.InlineData("frozen-fact ruling v1\nOrdinary prose")]
    [Xunit.InlineData("Frozen-fact ruling v1 extra\nOrdinary prose")]
    public async Task Answers_without_exact_first_line_header_keep_prose_behavior(string text)
    {
        using var fixture = new AnswerFixture();
        var intent = await fixture.Enqueue(text);
        Xunit.Assert.True(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Xunit.Assert.True(fixture.Request.IsCompleted);
        Xunit.Assert.Equal(text.Trim(), fixture.Request.Answer);
        Xunit.Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Xunit.Assert.NotNull((await fixture.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"))?.Receipt);
    }

    private sealed class AnswerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"answer-frozen-ruling-{Guid.NewGuid():N}");
        public AgentOrchestratorKernel Kernel { get; } = new();
        public TaskSpec Task { get; } = new(TaskId.New(), "Use the ruling", AgentRole.Developer);
        public Goal Goal { get; }
        public HumanInputRequest Request { get; }
        public SqliteOperatorIntentStore Intents { get; }
        public CollaborationItemStore Decisions { get; }
        public OperatorIntentCoordinator Coordinator { get; }

        public AnswerFixture()
        {
            Directory.CreateDirectory(_root);
            var workspace = OrchestratorWorkspace.ForDirectory(_root);
            Goal = Kernel.CreateGoal("Answer frozen fact question", [Task]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Request = Kernel.RequestHumanInput(Goal.Id, Task.Id, "Which edits are permitted?", HumanWaitKind.SpecClarification);
            Intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            Decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            Coordinator = new OperatorIntentCoordinator(Intents, decisions: Decisions, goalStateVersionResolver: _ => 0);
        }

        public Task<OperatorIntentRecord> Enqueue(string text)
        {
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                Request.Id.Value, Goal.Id.Value, text, OperatorActorKind.Human);
            return Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, Goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
