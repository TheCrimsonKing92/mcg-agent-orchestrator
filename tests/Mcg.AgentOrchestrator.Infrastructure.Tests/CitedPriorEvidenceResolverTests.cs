using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class CitedPriorEvidenceResolverTests : WorkerDispatchTestSupport
{
    private const string PriorGoalId = "cbf7b22e46664d2b899ff5ead8469b5b";
    private const string PriorTaskId = "aabbccdd11223344556677889900aabb";

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_surfaces_live_shaped_three_round_receipts")]
    public void SurfacesLiveShapedThreeRoundReceipts()
    {
        var snapshot = CreatePriorGoalSnapshot(
            PriorGoalId,
            PriorTaskId,
            [
                ("required-file-change-evidence-missing", 1, 0),
                ("required-file-change-evidence-missing", 1, 0),
                ("succeeded-dispatch-completion-evidence", 0, 0)
            ]);
        var reader = new FakeReader([snapshot]);
        var (goal, task) = CreateCurrentGoal("Resolve prior goal cbf7b22e before planning.");

        var artifact = new CitedPriorEvidenceResolver(reader).Resolve(goal, task);

        Assert.NotNull(artifact);
        Assert.Equal(1, reader.GoalReads);
        Assert.Equal(0, reader.TaskReads);
        Assert.Contains("prior_evidence_package=v1; cited_entities=1; packaged_entities=1", artifact, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(artifact!, "classifier_receipt: CLASSIFIER"));
        Assert.Equal(2, CountOccurrences(artifact!, "rule=required-file-change-evidence-missing"));
        Assert.Contains("rule=succeeded-dispatch-completion-evidence", artifact, StringComparison.Ordinal);
        Assert.Contains("root_exit_code=1", artifact, StringComparison.Ordinal);
        Assert.Contains("child_exit_code=0", artifact, StringComparison.Ordinal);
        Assert.Contains("worker_result=present", artifact, StringComparison.Ordinal);
        Assert.Contains("blockers=none", artifact, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_limits_twenty_rounds_to_eight_with_notice")]
    public void LimitsTwentyRoundsToEightWithNotice()
    {
        var rounds = Enumerable.Range(1, 20)
            .Select(number => ($"round-{number:D2}", number % 2, 0))
            .ToArray();
        var reader = new FakeReader([CreatePriorGoalSnapshot(PriorGoalId, PriorTaskId, rounds)]);
        var (goal, task) = CreateCurrentGoal("Use goal cbf7b22e evidence.");

        var artifact = new CitedPriorEvidenceResolver(reader).Resolve(goal, task)!;

        Assert.Contains("Rounds: 20; showing newest 8.", artifact, StringComparison.Ordinal);
        Assert.Contains("Truncated rounds: 12 older rounds omitted.", artifact, StringComparison.Ordinal);
        Assert.DoesNotContain("rule=round-12", artifact, StringComparison.Ordinal);
        Assert.Contains("rule=round-13", artifact, StringComparison.Ordinal);
        Assert.Contains("rule=round-20", artifact, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_collapses_duplicate_verification_rows_before_pairing_receipts")]
    public void CollapsesDuplicateVerificationRowsBeforePairingReceipts()
    {
        var snapshot = CreatePriorGoalSnapshot(
            PriorGoalId,
            PriorTaskId,
            Enumerable.Range(1, 9)
                .Select(number => ($"round-{number:D2}", number % 2, 0))
                .ToArray());
        var taskSnapshot = snapshot.Tasks.Single();
        snapshot = snapshot with
        {
            Tasks =
            [
                taskSnapshot with
                {
                    VerificationHistory = taskSnapshot.VerificationHistory!
                        .SelectMany(verification => Enumerable.Repeat(verification, 4))
                        .ToArray()
                }
            ]
        };
        var (goal, task) = CreateCurrentGoal("Use goal cbf7b22e evidence.");

        var artifact = new CitedPriorEvidenceResolver(new FakeReader([snapshot])).Resolve(goal, task)!;

        Assert.Contains("Rounds: 9; showing newest 8.", artifact, StringComparison.Ordinal);
        Assert.Contains("Truncated rounds: 1 older rounds omitted.", artifact, StringComparison.Ordinal);
        Assert.Equal(8, CountOccurrences(artifact, "- task="));
        Assert.Equal(8, CountOccurrences(artifact, "classifier_receipt: CLASSIFIER"));
        Assert.DoesNotContain("none stored for this round", artifact, StringComparison.Ordinal);
        Assert.DoesNotContain("rule=round-01", artifact, StringComparison.Ordinal);
        for (var round = 2; round <= 9; round++)
        {
            Assert.Equal(1, CountOccurrences(artifact, $"rule=round-{round:D2}"));
        }
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_prioritizes_explicit_citations_and_upgrades_bare_duplicates")]
    public void PrioritizesExplicitCitationsAndUpgradesBareDuplicates()
    {
        var snapshots = Enumerable.Range(1, 5)
            .Select(number => CreatePriorGoalSnapshot(
                $"{number}{new string((char)('0' + number), 7)}11223344556677889900aabb",
                $"a{number:D7}11223344556677889900aabb",
                []))
            .ToArray();
        var objective = "`11111111` `22222222` `33333333`; goal 33333333; goal 44444444; goal 55555555.";
        var (goal, task) = CreateCurrentGoal(objective);

        var artifact = new CitedPriorEvidenceResolver(new FakeReader(snapshots)).Resolve(goal, task)!;

        Assert.Contains("Citation selection order: explicit goal/task citations first, then bare identifiers; encounter order breaks ties.", artifact, StringComparison.Ordinal);
        Assert.Contains("prior_evidence_package=v1; cited_entities=5; packaged_entities=4", artifact, StringComparison.Ordinal);
        Assert.Contains("Truncated cited entities: 1 omitted after relevance ordering. Omitted citations: cited unspecified `22222222`.", artifact, StringComparison.Ordinal);
        Assert.DoesNotContain("## Cited unspecified `22222222`", artifact, StringComparison.Ordinal);
        Assert.True(artifact.IndexOf("## Cited goal `33333333`", StringComparison.Ordinal) < artifact.IndexOf("## Cited goal `44444444`", StringComparison.Ordinal));
        Assert.True(artifact.IndexOf("## Cited goal `44444444`", StringComparison.Ordinal) < artifact.IndexOf("## Cited goal `55555555`", StringComparison.Ordinal));
        Assert.True(artifact.IndexOf("## Cited goal `55555555`", StringComparison.Ordinal) < artifact.IndexOf("## Cited goal `11111111`", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_distinguishes_missing_round_receipt_from_lookup_failure")]
    public void DistinguishesMissingRoundReceiptFromLookupFailure()
    {
        var snapshot = CreatePriorGoalSnapshot(PriorGoalId, PriorTaskId, [("stored-nowhere", 0, 0)]) with
        {
            Timeline = []
        };
        var (goal, task) = CreateCurrentGoal("Use goal cbf7b22e evidence.");

        var withoutReceipt = new CitedPriorEvidenceResolver(new FakeReader([snapshot])).Resolve(goal, task)!;
        var failedLookup = new CitedPriorEvidenceResolver(new ThrowingReader()).Resolve(goal, task)!;

        Assert.Contains("classifier_receipt: unavailable (none stored for this round)", withoutReceipt, StringComparison.Ordinal);
        Assert.DoesNotContain("lookup failed", withoutReceipt, StringComparison.Ordinal);
        Assert.Contains("Records unavailable for cited goal `cbf7b22e`: historical store lookup failed (InvalidOperationException: fixture lookup failure)", failedLookup, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_task_citation_limits_evidence_to_that_task")]
    public void TaskCitationLimitsEvidenceToThatTask()
    {
        var cited = CreatePriorGoalSnapshot(
            PriorGoalId,
            PriorTaskId,
            [("cited-task-rule", 0, 0)]);
        var other = CreatePriorGoalSnapshot(
            PriorGoalId,
            "1234abcd11223344556677889900aabb",
            [("other-task-rule", 1, 1)]);
        var snapshot = cited with
        {
            Tasks = [cited.Tasks[0], other.Tasks[0]],
            Timeline = cited.Timeline.Concat(other.Timeline).ToArray()
        };
        var reader = new FakeReader([snapshot]);
        var (goal, task) = CreateCurrentGoal("Inspect task aabbccdd evidence.");

        var artifact = new CitedPriorEvidenceResolver(reader).Resolve(goal, task)!;

        Assert.Equal(0, reader.GoalReads);
        Assert.Equal(1, reader.TaskReads);
        Assert.Contains("rule=cited-task-rule", artifact, StringComparison.Ordinal);
        Assert.DoesNotContain("rule=other-task-rule", artifact, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_applies_utf8_byte_cap_with_notice")]
    public void AppliesUtf8ByteCapWithNotice()
    {
        var snapshots = Enumerable.Range(1, 3)
            .Select(entity => CreatePriorGoalSnapshot(
                $"{entity:D8}11223344556677889900aabb",
                $"{entity + 10:D8}11223344556677889900aabb",
                Enumerable.Range(1, 8)
                    .Select(round => ($"entity-{entity}-round-{round}-" + new string('x', 1100), 1, 0))
                    .ToArray()))
            .ToArray();
        var citations = string.Join(" ", snapshots.Select(snapshot => $"goal {snapshot.Id[..8]}")) + " goal feedface";
        var (goal, task) = CreateCurrentGoal(citations);

        var artifact = new CitedPriorEvidenceResolver(new FakeReader(snapshots)).Resolve(goal, task)!;

        Assert.True(Encoding.UTF8.GetByteCount(artifact) <= CitedPriorEvidenceResolver.MaxUtf8Bytes);
        Assert.Contains("UTF-8 byte cap reached", artifact, StringComparison.Ordinal);
        foreach (var snapshot in snapshots)
        {
            Assert.Matches(
                $@"UTF-8 byte cap omission for cited goal `{snapshot.Id[..8]}`: [1-8] resolved rounds omitted; [0-7] newest rounds included\.",
                artifact);
        }
        Assert.Contains("## Cited goal `feedface`", artifact, StringComparison.Ordinal);
        Assert.Contains("Records unavailable for cited goal `feedface`", artifact, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_no_citation_performs_no_read_and_creates_no_artifact")]
    public void NoCitationPerformsNoReadAndCreatesNoArtifact()
    {
        var reader = new FakeReader([CreatePriorGoalSnapshot(PriorGoalId, PriorTaskId, [])]);
        var resolver = new CitedPriorEvidenceResolver(reader);
        var (goal, task) = CreateCurrentGoal("Implement the current behavior without historical evidence.");
        var workingDirectory = CreateTempDirectory();

        var artifact = resolver.Resolve(goal, task);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory, citedPriorEvidence: "stale cited evidence");
        Assert.True(File.Exists(Path.Combine(contextDirectory, "packages", task.Id.Value, "prior-goal-evidence.md")));
        contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory, citedPriorEvidence: artifact);

        Assert.Null(artifact);
        Assert.Equal(0, reader.GoalReads);
        Assert.Equal(0, reader.TaskReads);
        Assert.False(File.Exists(Path.Combine(contextDirectory, "prior-goal-evidence.md")));
        Assert.False(File.Exists(Path.Combine(contextDirectory, "packages", task.Id.Value, "prior-goal-evidence.md")));
        Assert.DoesNotContain("- prior-goal-evidence.md:", File.ReadAllText(Path.Combine(contextDirectory, "manifest.md")), StringComparison.Ordinal);
        Assert.DoesNotContain("prior-goal-evidence.md", File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")), StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_reports_missing_and_ambiguous_selectors")]
    public void ReportsMissingAndAmbiguousSelectors()
    {
        var first = CreatePriorGoalSnapshot("deadbeef11223344556677889900aabb", PriorTaskId, []);
        var second = CreatePriorGoalSnapshot("deadbeefffeeddccbbaa009988776655", "bbccddee11223344556677889900aabb", []);
        var reader = new FakeReader([first, second]);
        var (goal, task) = CreateCurrentGoal("Compare goal deadbeef and task feedface.");

        var artifact = new CitedPriorEvidenceResolver(reader).Resolve(goal, task)!;

        Assert.Contains("Records unavailable for cited goal `deadbeef`: the cited goal prefix is ambiguous", artifact, StringComparison.Ordinal);
        Assert.Contains("Records unavailable for cited task `feedface`: no task id matched", artifact, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "CitedPriorEvidenceResolver_ignores_current_ids_and_commit_shas")]
    public void IgnoresCurrentIdsAndCommitShas()
    {
        var task = new TaskSpec(new TaskId("11223344556677889900aabbccddeeff"), "No prior evidence.", AgentRole.Planner);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            new GoalId("deadbeef11223344556677889900aabb"),
            "Current goal deadbeef; current task 11223344; commit `0123456789abcdef0123456789abcdef01234567`.",
            [task]);

        Assert.Empty(CitedPriorEvidenceResolver.ExtractSelectors(goal, task));
    }

    [Xunit.Fact(DisplayName = "PriorGoalEvidence_profile_dispatch_reads_sqlite_without_worker_start_or_state_write")]
    public async Task ProfileDispatchReadsSqliteWithoutWorkerStartOrStateWrite()
    {
        var root = CreateSeededDispatchRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var priorSnapshot = CreatePriorGoalSnapshot(
                PriorGoalId,
                PriorTaskId,
                [("succeeded-dispatch-completion-evidence", 0, 0)]);
            await repository.SaveAsync(AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([priorSnapshot], [])));
            var stateVersionBefore = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(workspace.SqliteStatePath, PriorGoalId);

            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement using prior goal cbf7b22e evidence.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Use prior goal cbf7b22e receipts.", [task]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Use prior goal cbf7b22e receipts.",
                ["The cited receipt is packaged before dispatch."],
                VerificationClass.TestVerifiable,
                [],
                []));
            var agent = new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("Test", "test-model", ModelCapability.Text, SubscriptionMode.ApiKey));
            kernel.ActivateGoal(goal.Id, [agent]);
            goal = kernel.GetGoal(goal.Id);
            task = goal.Tasks.Single();
            var gitStatusBefore = ReadGit(root, ["status", "--short"]);

            var dispatch = new GoalDispatchOperations().ProfileDispatchTask(
                kernel,
                workspace,
                goal,
                task,
                new WorkerProfile("echo", "echo {promptPath}"));

            var contextDirectory = Path.Combine(root, ".orchestrator-context", goal.Id.Value);
            var evidence = File.ReadAllText(Path.Combine(contextDirectory, "prior-goal-evidence.md"));
            Assert.Contains("root_exit_code=0", evidence, StringComparison.Ordinal);
            Assert.Contains("prior-goal-evidence.md", File.ReadAllText(Path.Combine(contextDirectory, "manifest.md")), StringComparison.Ordinal);
            Assert.Contains("prior-goal-evidence.md", File.ReadAllText(Path.Combine(contextDirectory, "artifact-registry.json")), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(contextDirectory, "packages", task.Id.Value, "prior-goal-evidence.md")));
            Assert.NotNull(task.LastDispatch);
            Assert.Null(task.LastProcess);
            Assert.True(File.Exists(dispatch.PromptPath));
            Assert.Equal(stateVersionBefore, await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(workspace.SqliteStatePath, PriorGoalId));
            Assert.Equal(gitStatusBefore, ReadGit(root, ["status", "--short"]));
        }
        finally
        {
            _ = GoalWorktrees.DeleteDirectoryWithRetry(root);
        }
    }

    private static (Goal Goal, TaskSpec Task) CreateCurrentGoal(string objective)
    {
        var task = new TaskSpec(new TaskId("ffeeddcc11223344556677889900aabb"), "Prepare current work.", AgentRole.Planner);
        var kernel = new AgentOrchestratorKernel();
        return (kernel.CreateGoal(new GoalId("00112233445566778899aabbccddeeff"), objective, [task]), task);
    }

    private static GoalSnapshot CreatePriorGoalSnapshot(
        string goalId,
        string taskId,
        IReadOnlyList<(string Rule, int RootExitCode, int ChildExitCode)> roundSpecs)
    {
        var startedAt = DateTimeOffset.Parse("2026-08-09T01:00:00Z");
        var verifications = roundSpecs.Select((round, index) =>
        {
            var completedAt = startedAt.AddMinutes(index + 1);
            return new TaskVerificationSnapshot(
                "verify",
                "C:\\repo",
                round.RootExitCode,
                WorkerResult(),
                string.Empty,
                completedAt,
                WorkerResultPresent: true,
                DispatchStartedAt: completedAt.AddSeconds(-30),
                ChildExitCode: round.ChildExitCode);
        }).ToArray();
        var timeline = roundSpecs.Select((round, index) =>
        {
            var verification = verifications[index];
            return new ProgressEventSnapshot(
                goalId,
                taskId,
                ProgressKind.TaskNote,
                $"CLASSIFIER rule={round.Rule}; outcome_class={(round.RootExitCode == 0 ? "verified-success" : "worker-output-failure")}; exit_code={round.RootExitCode}; root_exit_code={round.RootExitCode}; child_exit_code={round.ChildExitCode}; exit_artifact=verification-record; duration_ms=30000; worker_result=present; blockers=none; commit=none; verdict={(round.RootExitCode == 0 ? "VerifiedSuccess" : "NeedsRetry")}",
                verification.CompletedAt.AddMilliseconds(1));
        }).ToArray();
        var task = new TaskSnapshot(
            taskId,
            "Historical worker round.",
            AgentRole.Planner,
            WorkTaskStatus.Completed,
            null,
            null,
            verifications.LastOrDefault(),
            verifications,
            null,
            null);
        return new GoalSnapshot(goalId, "Historical goal.", GoalStatus.Completed, [task], timeline);
    }

    private static string WorkerResult() =>
        $"""
        WORKER_RESULT:
        files: none
        commands: none
        tests: deferred - historical verification
        commit: none
        blockers: none
        model_fit: OpenAI/{AgentCatalog.OpenAiSolSubscriptionModelAlias} - adequate - historical fixture
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    private static int CountOccurrences(string text, string value) =>
        Regex.Matches(text, Regex.Escape(value), RegexOptions.CultureInvariant).Count;

    private sealed class FakeReader(IReadOnlyList<GoalSnapshot> goals) : ICitedPriorEvidenceReader
    {
        public int GoalReads { get; private set; }
        public int TaskReads { get; private set; }

        public IReadOnlyList<GoalSnapshot> FindGoals(string idPrefix, int limit)
        {
            GoalReads++;
            return goals.Where(goal => goal.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray();
        }

        public IReadOnlyList<CitedPriorTaskMatch> FindTasks(string idPrefix, int limit)
        {
            TaskReads++;
            return goals
                .SelectMany(goal => goal.Tasks.Select(task => new CitedPriorTaskMatch(goal, task)))
                .Where(match => match.Task.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
        }
    }

    private sealed class ThrowingReader : ICitedPriorEvidenceReader
    {
        public IReadOnlyList<GoalSnapshot> FindGoals(string idPrefix, int limit) =>
            throw new InvalidOperationException("fixture lookup failure");

        public IReadOnlyList<CitedPriorTaskMatch> FindTasks(string idPrefix, int limit) =>
            throw new InvalidOperationException("fixture lookup failure");
    }
}
