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
            using (var workspace = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings))
            {
                firstTree = workspace.TreeRevision;
                firstIdentity = MergeTrainIdentity.Create(workspace.Members, main, firstTree, "manifest-v1").Value;
            }

            var initialSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.True(SpinWait.SpinUntil(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() > initialSecond,
                TimeSpan.FromSeconds(3)), "The UTC second did not advance before rematerialization.");
            using var again = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings);
            Assert.Equal(firstTree, again.TreeRevision);
            Assert.Equal(firstIdentity,
                MergeTrainIdentity.Create(again.Members, main, again.TreeRevision, "manifest-v1").Value);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
