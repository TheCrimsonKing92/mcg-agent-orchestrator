using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalChangesReaderTests
{
    // ── ParseFileList ──────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ParseFileList_returns_empty_for_empty_input")]
    public void ParseFileListReturnsEmptyForEmptyInput()
    {
        var result = GoalChangesReader.ParseFileList(string.Empty);
        Assert.Empty(result);
    }

    [Xunit.Fact(DisplayName = "ParseFileList_splits_newline_separated_paths")]
    public void ParseFileListSplitsNewlineSeparatedPaths()
    {
        var output = "src/Foo.cs\nsrc/Bar.cs\n";
        var result = GoalChangesReader.ParseFileList(output);
        Assert.Equal(2, result.Count);
        Assert.Contains("src/Bar.cs", result);
        Assert.Contains("src/Foo.cs", result);
    }

    [Xunit.Fact(DisplayName = "ParseFileList_deduplicates_and_sorts")]
    public void ParseFileListDeduplicatesAndSorts()
    {
        var output = "src/Z.cs\nsrc/A.cs\nsrc/Z.cs\n";
        var result = GoalChangesReader.ParseFileList(output);
        Assert.Equal(2, result.Count);
        Assert.Equal("src/A.cs", result[0]);
        Assert.Equal("src/Z.cs", result[1]);
    }

    // ── ParseStatusOutput ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ParseStatusOutput_returns_empty_for_empty_input")]
    public void ParseStatusOutputReturnsEmptyForEmptyInput()
    {
        var result = GoalChangesReader.ParseStatusOutput(string.Empty);
        Assert.Empty(result);
    }

    [Xunit.Fact(DisplayName = "ParseStatusOutput_strips_two_char_status_prefix")]
    public void ParseStatusOutputStripsStatusPrefix()
    {
        var output = "M  src/Foo.cs\n?? src/New.cs\n D src/Old.cs\n";
        var result = GoalChangesReader.ParseStatusOutput(output);
        Assert.Equal(3, result.Count);
        Assert.Contains("src/Foo.cs", result);
        Assert.Contains("src/New.cs", result);
        Assert.Contains("src/Old.cs", result);
    }

    // ── GoalChangesReader.Build — exact attribution ────────────────────────────

    [Xunit.Fact(DisplayName = "Build_returns_exact_files_when_base_and_result_commits_present")]
    public void BuildReturnsExactFilesWhenBothCommitsPresent()
    {
        var (kernel, goalId, taskId) = CreateRunningDispatch("abc1234", "def5678");
        var goal = kernel.Goals.First(g => g.Id == goalId);

        var captured = new List<(string dir, string[] args)>();
        WithFakeGit(
            (dir, args) =>
            {
                captured.Add((dir, args));
                return new GitCli.GitResult(0, "src/Foo.cs\nsrc/Bar.cs\n", string.Empty);
            },
            () =>
            {
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: true, showWorking: false, null, null);
                Assert.Single(report.Entries);
                var entry = report.Entries[0];
                Assert.Equal(GoalChangesAttribution.Exact, entry.Attribution);
                Assert.Equal(2, entry.Files.Count);
                Assert.Equal("abc1234", entry.BaseCommit);
                Assert.Equal("def5678", entry.ResultCommit);
                Assert.Contains("src/Bar.cs", entry.Files);
                Assert.Contains("src/Foo.cs", entry.Files);
                // git diff abc1234..def5678 --name-only must have been called
                Assert.Single(captured);
                Assert.Contains("abc1234..def5678", captured[0].args);
                Assert.Contains("--name-only", captured[0].args);
            });
    }

    [Xunit.Fact(DisplayName = "Build_returns_no_committed_entries_when_showCommitted_false")]
    public void BuildReturnsNoCommittedEntriesWhenShowCommittedFalse()
    {
        var (kernel, goalId, _) = CreateRunningDispatch("abc1234", "def5678");
        var goal = kernel.Goals.First(g => g.Id == goalId);

        WithFakeGit(
            (_, _) => new GitCli.GitResult(0, "src/Foo.cs\n", string.Empty),
            () =>
            {
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: false, showWorking: true, null, null);
                Assert.Empty(report.Entries);
            });
    }

    // ── GoalChangesReader.Build — uncommitted / in-flight ─────────────────────

    [Xunit.Fact(DisplayName = "Build_returns_git_status_files_for_in_flight_dispatch")]
    public void BuildReturnsGitStatusFilesForInFlightDispatch()
    {
        var (kernel, goalId, _) = CreateRunningDispatch(null, null);
        var goal = kernel.Goals.First(g => g.Id == goalId);

        WithFakeGit(
            (_, args) =>
            {
                if (args.Contains("status"))
                    return new GitCli.GitResult(0, "M  src/InFlight.cs\n", string.Empty);
                return new GitCli.GitResult(0, string.Empty, string.Empty);
            },
            () =>
            {
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: true, showWorking: true, null, null);
                Assert.Single(report.Entries);
                var entry = report.Entries[0];
                Assert.Equal(GoalChangesAttribution.Uncommitted, entry.Attribution);
                Assert.Single(entry.Files);
                Assert.Equal("src/InFlight.cs", entry.Files[0]);
            });
    }

    [Xunit.Fact(DisplayName = "BuildLiveDispatchSnapshot_combines_base_to_head_diff_and_working_files")]
    public void BuildLiveDispatchSnapshotCombinesCommittedAndWorkingFiles()
    {
        var captured = new List<string[]>();
        WithFakeGit(
            (_, args) =>
            {
                captured.Add(args);
                if (args.Contains("diff"))
                    return new GitCli.GitResult(0, "src/Committed.cs\nsrc/Shared.cs\n", string.Empty);
                if (args.Contains("status"))
                    return new GitCli.GitResult(0, "M  src/Working.cs\n?? src/Shared.cs\n", string.Empty);
                return new GitCli.GitResult(1, string.Empty, "unexpected");
            },
            () =>
            {
                var snapshot = GoalChangesReader.BuildLiveDispatchSnapshot("/fake/worktree", "abc123", displayLimit: 3);

                Assert.Equal(3, snapshot.Files.Count);
                Assert.Contains("src/Committed.cs", snapshot.Files);
                Assert.Contains("src/Shared.cs", snapshot.Files);
                Assert.Contains("src/Working.cs", snapshot.Files);
                Assert.Equal(3, snapshot.DisplayFiles.Count);
                Assert.Equal(0, snapshot.RemainingFileCount);
                Xunit.Assert.Contains(captured, args => args.Contains("abc123..HEAD"));
                Xunit.Assert.Contains(captured, args => args.Contains("status"));
            });
    }

    [Xunit.Fact(DisplayName = "Build_returns_no_working_entries_when_showWorking_false")]
    public void BuildReturnsNoWorkingEntriesWhenShowWorkingFalse()
    {
        var (kernel, goalId, _) = CreateRunningDispatch(null, null);
        var goal = kernel.Goals.First(g => g.Id == goalId);

        WithFakeGit(
            (_, _) => new GitCli.GitResult(0, "M  src/InFlight.cs\n", string.Empty),
            () =>
            {
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: true, showWorking: false, null, null);
                Assert.Empty(report.Entries);
            });
    }

    // ── GoalChangesReader.Build — legacy / approximate ────────────────────────

    [Xunit.Fact(DisplayName = "Build_returns_approximate_attribution_for_legacy_dispatch")]
    public void BuildReturnsApproximateAttributionForLegacyDispatch()
    {
        var (kernel, goalId, taskId) = CreateCompletedDispatch();
        var goal = kernel.Goals.First(g => g.Id == goalId);

        WithFakeGit(
            (_, args) =>
            {
                if (args.Contains("log"))
                    return new GitCli.GitResult(0, "src/Legacy.cs\n", string.Empty);
                return new GitCli.GitResult(0, string.Empty, string.Empty);
            },
            () =>
            {
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: true, showWorking: false, null, null);
                Assert.Single(report.Entries);
                var entry = report.Entries[0];
                Assert.Equal(GoalChangesAttribution.Approximate, entry.Attribution);
                Assert.Null(entry.BaseCommit);
                Assert.Null(entry.ResultCommit);
                Assert.Single(entry.Files);
                Assert.Equal("src/Legacy.cs", entry.Files[0]);
            });
    }

    // ── Role filter ────────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Build_filters_by_role")]
    public void BuildFiltersByRole()
    {
        var kernel = new AgentOrchestratorKernel();
        var devTask = new TaskSpec(TaskId.New(), "Dev task", AgentRole.Developer);
        var testTask = new TaskSpec(TaskId.New(), "Test task", AgentRole.Tester);
        var goal = kernel.CreateGoal("Test goal", [devTask, testTask]);
        var agents = BuildAgents(AgentRole.Developer, AgentRole.Tester);
        kernel.ActivateGoal(goal.Id, agents);

        var dispatch = new TaskDispatchRecord("claude", "claude -p ...", "/wt", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, devTask.Id, dispatch);
        kernel.RecordDispatchBaseCommit(goal.Id, devTask.Id, "aaa");
        kernel.RecordDispatchResultCommit(goal.Id, devTask.Id, "bbb");

        kernel.RecordTaskDispatch(goal.Id, testTask.Id, dispatch with { DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        kernel.RecordDispatchBaseCommit(goal.Id, testTask.Id, "ccc");
        kernel.RecordDispatchResultCommit(goal.Id, testTask.Id, "ddd");

        WithFakeGit(
            (_, _) => new GitCli.GitResult(0, "src/File.cs\n", string.Empty),
            () =>
            {
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: true, showWorking: true, "Developer", null);
                Assert.Single(report.Entries);
                Assert.Equal(AgentRole.Developer, report.Entries[0].Role);
            });
    }

    // ── Task filter ────────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Build_filters_by_task_id_prefix")]
    public void BuildFiltersByTaskIdPrefix()
    {
        var kernel = new AgentOrchestratorKernel();
        var task1 = new TaskSpec(TaskId.New(), "Task one", AgentRole.Developer);
        var task2 = new TaskSpec(TaskId.New(), "Task two", AgentRole.Developer);
        var goal = kernel.CreateGoal("Test goal", [task1, task2]);
        var agents = BuildAgents(AgentRole.Developer);
        kernel.ActivateGoal(goal.Id, agents);

        var dispatch = new TaskDispatchRecord("claude", "claude -p ...", "/wt", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task1.Id, dispatch);
        kernel.RecordDispatchBaseCommit(goal.Id, task1.Id, "aaa");
        kernel.RecordDispatchResultCommit(goal.Id, task1.Id, "bbb");

        kernel.RecordTaskDispatch(goal.Id, task2.Id, dispatch with { DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        kernel.RecordDispatchBaseCommit(goal.Id, task2.Id, "ccc");
        kernel.RecordDispatchResultCommit(goal.Id, task2.Id, "ddd");

        WithFakeGit(
            (_, _) => new GitCli.GitResult(0, "src/File.cs\n", string.Empty),
            () =>
            {
                var prefix = task1.Id.Value[..8];
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: true, showWorking: true, null, prefix);
                Assert.Single(report.Entries);
                Assert.Equal(task1.Id.Value, report.Entries[0].TaskId);
            });
    }

    // ── Grouping by role ────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Build_groups_entries_by_sdlc_role_order")]
    public void BuildGroupsEntriesBySdlcRoleOrder()
    {
        var kernel = new AgentOrchestratorKernel();
        var testerTask = new TaskSpec(TaskId.New(), "Tester task", AgentRole.Tester);
        var devTask = new TaskSpec(TaskId.New(), "Dev task", AgentRole.Developer);
        var goal = kernel.CreateGoal("Test goal", [testerTask, devTask]);
        var agents = BuildAgents(AgentRole.Developer, AgentRole.Tester);
        kernel.ActivateGoal(goal.Id, agents);

        var dispatch = new TaskDispatchRecord("claude", "claude -p ...", "/wt", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, testerTask.Id, dispatch);
        kernel.RecordDispatchBaseCommit(goal.Id, testerTask.Id, "t1");
        kernel.RecordDispatchResultCommit(goal.Id, testerTask.Id, "t2");

        kernel.RecordTaskDispatch(goal.Id, devTask.Id, dispatch with { DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        kernel.RecordDispatchBaseCommit(goal.Id, devTask.Id, "d1");
        kernel.RecordDispatchResultCommit(goal.Id, devTask.Id, "d2");

        WithFakeGit(
            (_, _) => new GitCli.GitResult(0, string.Empty, string.Empty),
            () =>
            {
                var report = GoalChangesReader.Build(goal, "/fake/worktree", showCommitted: true, showWorking: false, null, null);
                Assert.Equal(2, report.Entries.Count);
                // Developer (order 3) should come before Tester (order 4)
                Assert.Equal(AgentRole.Developer, report.Entries[0].Role);
                Assert.Equal(AgentRole.Tester, report.Entries[1].Role);
            });
    }

    // ── Kernel commit recording ────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Kernel_RecordDispatchBaseCommit_stores_on_LastDispatch")]
    public void KernelRecordDispatchBaseCommitStoresOnLastDispatch()
    {
        var (kernel, goalId, taskId) = CreateRunningDispatch(null, null);
        kernel.RecordDispatchBaseCommit(goalId, taskId, "abc123");
        var task = kernel.GetTask(goalId, taskId);
        Assert.Equal("abc123", task.LastDispatch?.BaseCommit);
    }

    [Xunit.Fact(DisplayName = "Kernel_RecordDispatchResultCommit_stores_on_LastDispatch")]
    public void KernelRecordDispatchResultCommitStoresOnLastDispatch()
    {
        var (kernel, goalId, taskId) = CreateRunningDispatch("abc123", null);
        kernel.RecordDispatchResultCommit(goalId, taskId, "def456");
        var task = kernel.GetTask(goalId, taskId);
        Assert.Equal("def456", task.LastDispatch?.ResultCommit);
    }

    [Xunit.Fact(DisplayName = "Commits_survive_snapshot_round_trip")]
    public void CommitsSurviveSnapshotRoundTrip()
    {
        var (kernel, goalId, taskId) = CreateRunningDispatch("abc123", "def456");

        var snapshot = kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot);

        var task = restored.GetTask(goalId, taskId);
        Assert.Equal("abc123", task.LastDispatch?.BaseCommit);
        Assert.Equal("def456", task.LastDispatch?.ResultCommit);
    }

    // ── ApplyRefreshOutcome sets result commit ─────────────────────────────────

    [Xunit.Fact(DisplayName = "ApplyRefreshOutcome_records_result_commit_when_present")]
    public void ApplyRefreshOutcomeRecordsResultCommitWhenPresent()
    {
        var (kernel, goalId, taskId) = CreateRunningDispatch("abc123", null);
        var task = kernel.GetTask(goalId, taskId);

        // Simulate a completed process record
        var processRecord = new TaskProcessRecord(
            1234, task.LastDispatch!.Command, task.LastDispatch.WorkingDirectory,
            "/out.log", "/err.log", "/exit.txt",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30), 0);
        kernel.RecordTaskProcessStarted(goalId, taskId, processRecord);

        var completedProcess = processRecord with { CompletedAt = DateTimeOffset.UtcNow.AddSeconds(30), ExitCode = 0 };
        var verification = new TaskVerificationRecord(
            task.LastDispatch.Command, task.LastDispatch.WorkingDirectory,
            0, "WORKER_RESULT:\nfiles: src/Foo.cs\nEND_WORKER_RESULT", string.Empty, DateTimeOffset.UtcNow);
        var outcome = new DispatchRefreshOutcome(completedProcess, verification, "deadbeef");

        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goalId, taskId, outcome);

        var refreshed = kernel.GetTask(goalId, taskId);
        Assert.Equal("deadbeef", refreshed.LastDispatch?.ResultCommit);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
        CreateRunningDispatch(string? baseCommit, string? resultCommit)
    {
        var kernel = new AgentOrchestratorKernel();
        var taskId = TaskId.New();
        var task = new TaskSpec(taskId, "Implement changes", AgentRole.Developer);
        var goal = kernel.CreateGoal("Test goal", [task]);
        var agents = BuildAgents(AgentRole.Developer);
        kernel.ActivateGoal(goal.Id, agents);

        var dispatch = new TaskDispatchRecord("claude", "claude -p ...", "/worktree", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, taskId, dispatch);

        if (baseCommit is not null)
            kernel.RecordDispatchBaseCommit(goal.Id, taskId, baseCommit);
        if (resultCommit is not null)
            kernel.RecordDispatchResultCommit(goal.Id, taskId, resultCommit);

        return (kernel, goal.Id, taskId);
    }

    private static (AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
        CreateCompletedDispatch()
    {
        var (kernel, goalId, taskId) = CreateRunningDispatch(null, null);
        var task = kernel.GetTask(goalId, taskId);
        var verification = new TaskVerificationRecord(
            task.LastDispatch!.Command, task.LastDispatch.WorkingDirectory,
            0, "WORKER_RESULT:\nfiles: src/Foo.cs\nEND_WORKER_RESULT", string.Empty, DateTimeOffset.UtcNow);
        kernel.RecordTaskVerification(goalId, taskId, verification);
        return (kernel, goalId, taskId);
    }

    private static IReadOnlyList<AgentDefinition> BuildAgents(params AgentRole[] roles)
    {
        var capability = ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse;
        return roles
            .Select(role => new AgentDefinition(AgentId.New(), role.ToString(), role,
                new ModelProfile("OpenAI", "gpt-4", capability, SubscriptionMode.ApiKey)))
            .ToArray();
    }

    private static void WithFakeGit(
        Func<string, string[], GitCli.GitResult> fake,
        Action action)
    {
        var saved = GoalChangesReader.RunGit;
        GoalChangesReader.RunGit = fake;
        try
        {
            action();
        }
        finally
        {
            GoalChangesReader.RunGit = saved;
        }
    }

    private static bool Empty<T>(IReadOnlyList<T> list) => list.Count == 0;
}

file static class Assert
{
    public static void Equal<T>(T expected, T actual) =>
        Xunit.Assert.Equal(expected, actual);

    public static void Single<T>(IReadOnlyList<T> collection) =>
        Xunit.Assert.Single(collection);

    public static void Single<T>(IEnumerable<T> collection) =>
        Xunit.Assert.Single(collection);

    public static void Empty<T>(IReadOnlyList<T> collection) =>
        Xunit.Assert.Empty(collection);

    public static void Empty<T>(IEnumerable<T> collection) =>
        Xunit.Assert.Empty(collection);

    public static void Contains<T>(T item, IEnumerable<T> collection) =>
        Xunit.Assert.Contains(item, collection);

    public static void Null(object? value) =>
        Xunit.Assert.Null(value);

    public static void NotNull(object? value) =>
        Xunit.Assert.NotNull(value);
}
