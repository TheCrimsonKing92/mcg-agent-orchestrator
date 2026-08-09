using Mcg.AgentOrchestrator.Core;

public sealed class SandboxCommitBlockedFailureTests
{
    private const string WorkerResultStdout =
        "Implemented the removal, but blocked on committing by `.git` metadata permissions.\n" +
        "WORKER_RESULT:\nfiles: src/Foo.cs, tests/FooTests.cs\nmodel_fit: adequate\nEND_WORKER_RESULT";

    private const string CompletedWorkerResultStdout =
        "Implemented the requested changes.\n" +
        "WORKER_RESULT:\nfiles: src/Foo.cs, tests/FooTests.cs\nmodel_fit: adequate\nEND_WORKER_RESULT";

    private static TaskVerificationRecord Verification(int exitCode, string stdout, string stderr) =>
        new("codex exec", "C:\\repo", exitCode, stdout, stderr, DateTimeOffset.UtcNow);

    private static TaskSpec Task(AgentRole role = AgentRole.Developer) =>
        new(TaskId.New(), $"{role} task", role);

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_true_for_index_lock_with_worker_result")]
    public void TrueForIndexLockWithWorkerResult()
    {
        var verification = Verification(
            1,
            WorkerResultStdout,
            "fatal: Unable to create '.../.git/worktrees/abc/index.lock': Permission denied");

        Assert.True(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_true_for_blocked_on_committing_phrase")]
    public void TrueForBlockedOnCommittingPhrase()
    {
        var verification = Verification(1, WorkerResultStdout, "external ACL prevented commit");

        Assert.True(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_false_for_low_integrity_1312_logon_session_evidence")]
    public void FalseForLowIntegrity1312LogonSessionEvidence()
    {
        var verification = Verification(
            1,
            CompletedWorkerResultStdout,
            "dotnet.cmd: CreateProcessAsUserW 1312: A specified logon session does not exist. It may already have been terminated.");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_false_for_low_integrity_git_1312_evidence")]
    public void FalseForLowIntegrityGit1312Evidence()
    {
        var verification = Verification(
            1,
            CompletedWorkerResultStdout,
            "git.exe: CreateProcessAsUserW failed 1312: A specified logon session does not exist.");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_true_when_1312_and_positive_commit_block_evidence_overlap")]
    public void TrueWhen1312AndPositiveCommitBlockEvidenceOverlap()
    {
        var verification = Verification(
            1,
            WorkerResultStdout,
            "git.exe: CreateProcessAsUserW failed 1312: A specified logon session does not exist.");

        Assert.True(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_reads_log_paths_for_evidence_outside_retained_excerpt")]
    public void ReadsLogPathsForEvidenceOutsideRetainedExcerpt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-sandbox-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var stdoutPath = Path.Combine(root, "out.log");
            var stderrPath = Path.Combine(root, "err.log");
            var fullStdout =
                new string('A', VerificationTextBounds.PreviewHeadChars) +
                "\nWORKER_RESULT:\nfiles: src/Foo.cs\ntests: pass\nblockers: none\nEND_WORKER_RESULT\n" +
                new string('Z', VerificationTextBounds.PreviewTailChars);
            var fullStderr =
                new string('B', VerificationTextBounds.PreviewHeadChars) +
                "\nfatal: Unable to create '.git/index.lock': Permission denied\n" +
                new string('Y', VerificationTextBounds.PreviewTailChars);
            File.WriteAllText(stdoutPath, fullStdout);
            File.WriteAllText(stderrPath, fullStderr);
            var verification = new TaskVerificationRecord(
                "codex exec",
                root,
                1,
                VerificationTextBounds.BoundText(fullStdout, stdoutPath),
                VerificationTextBounds.BoundText(fullStderr, stderrPath),
                DateTimeOffset.UtcNow,
                StandardOutputPath: stdoutPath,
                StandardErrorPath: stderrPath);

            Assert.DoesNotContain("WORKER_RESULT", verification.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("index.lock", verification.StandardError, StringComparison.Ordinal);
            Assert.True(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_false_when_succeeded")]
    public void FalseWhenSucceeded()
    {
        var verification = Verification(0, WorkerResultStdout, "index.lock: Permission denied");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_false_without_useful_work")]
    public void FalseWithoutUsefulWork()
    {
        // Commit-block signature present, but the worker produced no evidence of having done work.
        var verification = Verification(
            1,
            "Starting up...",
            "fatal: Unable to create '.git/index.lock': Permission denied");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_false_for_real_failure_without_commit_block")]
    public void FalseForRealFailureWithoutCommitBlock()
    {
        // Useful work reported, but the failure is a genuine compile failure with no commit-block
        // signature anywhere — must not be treated as a benign sandbox block.
        var verification = Verification(
            1,
            "WORKER_RESULT:\nfiles: src/Foo.cs\nmodel_fit: adequate\nEND_WORKER_RESULT",
            "error CS0103: The name 'Foo' does not exist in the current context");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(), verification));
    }

    [Xunit.Theory(DisplayName = "IsSandboxCommitBlockedFailure_false_for_read_only_and_unknown_roles")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    [Xunit.InlineData((AgentRole)999)]
    public void FalseForReadOnlyAndUnknownRoles(AgentRole role)
    {
        var verification = Verification(
            1,
            WorkerResultStdout,
            "fatal: Unable to create '.../.git/worktrees/abc/index.lock': Permission denied");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(role), verification));
        Assert.NotEqual(
            DispatchOutcomeKind.SandboxCommitBlocked,
            DispatchFailureClassifier.Classify(Task(role), verification).Kind);
    }

    [Xunit.Theory(DisplayName = "IsSandboxCommitBlockedFailure_preserves_file_role_commit_block_classification")]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void PreservesFileRoleCommitBlockClassification(AgentRole role)
    {
        var verification = Verification(
            1,
            WorkerResultStdout,
            "fatal: Unable to create '.../.git/worktrees/abc/index.lock': Permission denied");

        Assert.True(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(Task(role), verification));
    }

    [Xunit.Theory(DisplayName = "IsSandboxCommitBlockedFailure_excludes_read_only_roles_under_a_recorded_dispatch")]
    [Xunit.InlineData(AgentRole.Planner)]
    [Xunit.InlineData(AgentRole.Researcher)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void ExcludesReadOnlyRolesUnderRecordedDispatch(AgentRole role)
    {
        var verification = Verification(
            1,
            WorkerResultStdout,
            "fatal: Unable to create '.../.git/worktrees/abc/index.lock': Permission denied");

        var root = Path.Combine(Path.GetTempPath(), $"mcg-sandbox-role-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeClock();
            var kernel = new AgentOrchestratorKernel(clock);
            var goal = kernel.CreateGoal("Classify sandbox evidence");
            kernel.ActivateGoal(goal.Id, DefaultAgents());
            var task = goal.Tasks.First(candidate => candidate.RequiredRole == role);
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord("codex-cli", "opaque command", root, clock.UtcNow));

            Assert.NotNull(task.LastDispatch);
            Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(task, verification));
            Assert.NotEqual(
                DispatchOutcomeKind.SandboxCommitBlocked,
                DispatchFailureClassifier.Classify(task, verification).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
