using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class MergeTrainIdentityMaterializationTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void SameSelectionMaterializedInDifferentUtcSeconds_HasSameIdentity()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "src/First.cs", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "src/Second.cs", "second");
            var bindings = new[]
            {
                TrainBind(first.GoalId, first.Revision, "src/First.cs", "resource:first"),
                TrainBind(second.GoalId, second.Revision, "src/Second.cs", "resource:second")
            };
            string firstIdentity;
            string firstTree;
            string firstCommit;
            var firstDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var workspace = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, committerDate: firstDate))
            {
                firstTree = workspace.TreeRevision;
                firstCommit = workspace.CommitRevision;
                firstIdentity = MergeTrainIdentity.Create(workspace.Members, main, firstTree, "manifest-v1").Value;
            }

            using var again = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                committerDate: firstDate.AddSeconds(10));
            Assert.NotEqual(firstCommit, again.CommitRevision);
            Assert.Equal(firstTree, again.TreeRevision);
            Assert.Equal(firstIdentity,
                MergeTrainIdentity.Create(again.Members, main, again.TreeRevision, "manifest-v1").Value);
            using var defaultWorkspace = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default);
            Assert.Equal(firstTree, defaultWorkspace.TreeRevision);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
