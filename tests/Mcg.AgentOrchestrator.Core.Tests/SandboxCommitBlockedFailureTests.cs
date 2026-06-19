using Mcg.AgentOrchestrator.Core;

public sealed class SandboxCommitBlockedFailureTests
{
    private const string WorkerResultStdout =
        "Implemented the removal, but blocked on committing by `.git` metadata permissions.\n" +
        "WORKER_RESULT:\nfiles: src/Foo.cs, tests/FooTests.cs\nmodel_fit: adequate\nEND_WORKER_RESULT";

    private static TaskVerificationRecord Verification(int exitCode, string stdout, string stderr) =>
        new("codex exec", "C:\\repo", exitCode, stdout, stderr, DateTimeOffset.UtcNow);

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_true_for_index_lock_with_worker_result")]
    public void TrueForIndexLockWithWorkerResult()
    {
        var verification = Verification(
            1,
            WorkerResultStdout,
            "fatal: Unable to create '.../.git/worktrees/abc/index.lock': Permission denied");

        Assert.True(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_true_for_blocked_on_committing_phrase")]
    public void TrueForBlockedOnCommittingPhrase()
    {
        var verification = Verification(1, WorkerResultStdout, "external ACL prevented commit");

        Assert.True(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_false_when_succeeded")]
    public void FalseWhenSucceeded()
    {
        var verification = Verification(0, WorkerResultStdout, "index.lock: Permission denied");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(verification));
    }

    [Xunit.Fact(DisplayName = "IsSandboxCommitBlockedFailure_false_without_useful_work")]
    public void FalseWithoutUsefulWork()
    {
        // Commit-block signature present, but the worker produced no evidence of having done work.
        var verification = Verification(
            1,
            "Starting up...",
            "fatal: Unable to create '.git/index.lock': Permission denied");

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(verification));
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

        Assert.False(DispatchFailureClassifier.IsSandboxCommitBlockedFailure(verification));
    }
}
