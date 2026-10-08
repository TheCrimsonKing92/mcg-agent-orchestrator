using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static SliceBatchExecutionTests;

// Parallel-safe: the fixture owns its temporary workspace and kernel; no shared mutable state.
public sealed class SliceBatchCompositionHoldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AreAllChildrenStreamComplete_RequiresEveryChildAndCorrectParent(bool verifying)
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            Assert.False(SliceBatchParentExecutionGuard.AreAllChildrenStreamComplete(parent, []));
            Assert.False(SliceBatchParentExecutionGuard.AreAllChildrenStreamComplete(parent, children));
            CompleteTasks(kernel, children[0]);
            Assert.False(SliceBatchParentExecutionGuard.AreAllChildrenStreamComplete(parent, children));
            foreach (var child in children)
            {
                if (child.Status != GoalStatus.Verified) CompleteTasks(kernel, child);
                if (verifying) Assert.True(kernel.BeginGoalAcceptanceVerification(child.Id, "Verify ready child."));
            }
            Assert.True(SliceBatchParentExecutionGuard.AreAllChildrenStreamComplete(parent, children));
            var otherParent = kernel.CreateGoal("Unrelated parent.");
            Assert.False(SliceBatchParentExecutionGuard.AreAllChildrenStreamComplete(otherParent, children));
            Assert.False(SliceBatchParentExecutionGuard.AreAllChildrenStreamComplete(children[0], children));
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    [Fact]
    public void TryDescribeCompositionHold_Conflict_NamesParentChildAndSortedPaths()
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            var result = new StreamCompositionResult("commit", "tree", [children[0].Id],
                new(children[1].Id, MergeTrainEjectionReason.RebaseConflict,
                    ["src/Z.cs", "src/A.cs"], "rebase conflict"));

            var hold = SliceBatchParentExecutionGuard.TryDescribeCompositionHold(parent, result);

            Assert.NotNull(hold);
            Assert.Contains(parent.Id.Value[..8], hold);
            Assert.Contains(children[1].Id.Value[..8], hold);
            Assert.Contains("src/A.cs, src/Z.cs", hold);
            Assert.False((result with { BuildCheck = new(true, null, "ok") }).IsReleasable);
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    [Fact]
    public void TryDescribeCompositionHold_BuildFailure_NamesProjectAndComposedChildren()
    {
        var (kernel, workspace, agents, providers) = CreateContext(DisjointSliceBatchJson);
        try
        {
            var parent = CreateBatch(kernel, workspace, agents, providers);
            var children = kernel.Goals.Where(goal => goal.SliceBatchParentId == parent.Id).ToArray();
            var result = new StreamCompositionResult("commit", "tree", children.Select(child => child.Id).ToArray(),
                null, new(false, "src/Failing.csproj", "CS0001"));

            var hold = SliceBatchParentExecutionGuard.TryDescribeCompositionHold(parent, result);

            Assert.NotNull(hold);
            Assert.Contains(parent.Id.Value[..8], hold);
            foreach (var child in children) Assert.Contains(child.Id.Value[..8], hold);
            Assert.Contains("src/Failing.csproj", hold);
            Assert.False(result.IsReleasable);
            var missingBuild = result with { BuildCheck = null };
            Assert.False(missingBuild.IsReleasable);
            Assert.Null(SliceBatchParentExecutionGuard.TryDescribeCompositionHold(parent, missingBuild));
            var passed = result with { BuildCheck = new(true, null, "ok") };
            Assert.True(passed.IsReleasable);
            Assert.Null(SliceBatchParentExecutionGuard.TryDescribeCompositionHold(parent, passed));
        }
        finally { Directory.Delete(workspace.RootDirectory, recursive: true); }
    }

    private static void CompleteTasks(AgentOrchestratorKernel kernel, Goal goal)
    {
        var completedAt = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        foreach (var task in goal.Tasks)
        {
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test.exe", "unused", completedAt));
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", "unused", 0, "ok", "", completedAt));
        }
    }
}
