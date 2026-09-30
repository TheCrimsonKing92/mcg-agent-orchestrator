using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class InterruptedWorkCheckpointAuthorizerWorkerMessageTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void WorkerCommitAfterCheckpointKeepsReconciledDisposition(bool newFormat)
    {
        using var fixture = InterruptedWorkCheckpointAuthorizerTests.Fixture.Create();
        var first = fixture.Authorize();
        Xunit.Assert.Equal(InterruptedWorkCheckpointDispositionKind.Checkpointed, first.Kind);
        var checkpoint = first.Checkpoint!;
        Xunit.Assert.Equal(
            checkpoint.RenderCommitMessage(),
            GitCli.Run(fixture.Root, "log", "-1", "--format=%B").Output.Trim());
        Xunit.Assert.Equal(
            $"orchestrator: checkpoint interrupted {checkpoint.Role} work for task {checkpoint.TaskId[..Math.Min(8, checkpoint.TaskId.Length)]}\n\n" +
            $"Orchestrator-Checkpoint: interrupted-work\nCheckpoint-Key: {checkpoint.IdempotencyKey}\n" +
            $"Checkpoint-Dispatch-Id: {checkpoint.DispatchId}\nCheckpoint-Goal: {checkpoint.GoalId}\n" +
            $"Checkpoint-Task: {checkpoint.TaskId}\nCheckpoint-Role: {checkpoint.Role}\n" +
            $"Checkpoint-Branch: {checkpoint.Branch}\nCheckpoint-Parent: {checkpoint.ParentCommit}\n" +
            $"Checkpoint-Cause: {checkpoint.Cause}",
            checkpoint.RenderCommitMessage());

        File.AppendAllText(Path.Combine(fixture.Root, "src", "Feature.cs"), "\nfollow-up");
        var message = newFormat
            ? OrchestratorCommitMessage.ForWorker(
                new Goal(fixture.GoalId, "Checkpoint fixture", [fixture.Task]),
                fixture.Task,
                "dispatch-1",
                "Orchestrator-Checkpoint: interrupted-work",
                ["src/Feature.cs"])
            : new OrchestratorCommitMessage("Developer task.: Committed implementation.", string.Empty);
        var commit = fixture.Committer.TryCommitWorktreeEdits(
            fixture.Root, message, ["src/Feature.cs"]);
        Xunit.Assert.True(commit.Succeeded, commit.Diagnostic);
        var workerMessage = GitCli.Run(fixture.Root, "log", "-1", "--format=%B").Output;
        if (newFormat)
        {
            Xunit.Assert.DoesNotContain("Orchestrator-Checkpoint", workerMessage, StringComparison.OrdinalIgnoreCase);
        }

        fixture.RefreshInspection();
        var replay = fixture.Authorize();
        Xunit.Assert.Equal(InterruptedWorkCheckpointDispositionKind.Reconciled, replay.Kind);
        Xunit.Assert.Equal("checkpoint-reconciled", replay.Token);
    }

    [Xunit.Fact]
    public void WorkerBuildCheckRecoverySubjectHasNoBody()
    {
        using var fixture = InterruptedWorkCheckpointAuthorizerTests.Fixture.Create();
        var subject = $"checkpoint: worker-build-check-failed recovery 1/2 for task {fixture.Task.Id.Value}";
        var commit = fixture.Committer.TryCommitWorktreeEdits(
            fixture.Root, subject, ["src/Feature.cs"]);

        Xunit.Assert.True(commit.Succeeded, commit.Diagnostic);
        Xunit.Assert.Equal(subject, GitCli.Run(fixture.Root, "log", "-1", "--format=%B").Output.Trim());
    }
}
