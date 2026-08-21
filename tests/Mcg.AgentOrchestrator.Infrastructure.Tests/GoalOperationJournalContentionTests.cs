using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalOperationJournalContentionTests
{
    [Xunit.Fact]
    public void GoalOperationJournal_AppendWhileSharedReaderIsOpen_PreservesRawContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-goal-journal-contention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Journal contention");
            GoalOperationJournal.Begin(root, goal, "acceptance", "first");
            var path = GoalOperationJournal.PathFor(root, goal.Id);

            using (new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                GoalOperationJournal.Completed(root, goal, "acceptance", "second");
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"status\":\"Begin\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"detail\":\"first\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"status\":\"Completed\"", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"detail\":\"second\"", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
