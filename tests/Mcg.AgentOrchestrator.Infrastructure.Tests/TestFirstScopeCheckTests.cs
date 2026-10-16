using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static Mcg.AgentOrchestrator.App.Orchestration.TestFirstScopeCheck;

// Parallel-safe: every real git operation belongs to a unique disposable repository.
public sealed class TestFirstScopeCheckTests
{
    private const string TestPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WidgetCases.cs";
    private const string SourcePath = "src/Mcg.AgentOrchestrator.Core/Widget.cs";

    [Fact]
    public void Evaluate_Author_AllowsTestsAndAdditionsOnlySource()
    {
        var result = Evaluate(CheckpointKind.Author, [(12, 0, TestPath), (5, 0, SourcePath)], []);
        Assert.Equal(ScopeOutcome.Passed, result.Outcome);
        Assert.Empty(result.OffendingPaths);
    }

    [Fact]
    public void Evaluate_Author_RejectsRemovedSourceLinesOnly()
    {
        var result = Evaluate(CheckpointKind.Author, [(12, 0, TestPath), (4, 2, SourcePath)], []);
        Assert.Equal(ScopeOutcome.Failed, result.Outcome);
        Assert.Equal([SourcePath], result.OffendingPaths);
        Assert.Contains(SourcePath, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPath, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_Author_ListsEveryForeignPathInOrdinalOrder()
    {
        var result = Evaluate(CheckpointKind.Author, [(3, 0, "docs/widget.md"), (1, 0, "config/acceptance-manifest.json")], []);
        Assert.Equal(ScopeOutcome.Failed, result.Outcome);
        Assert.Equal(["config/acceptance-manifest.json", "docs/widget.md"], result.OffendingPaths);
        Assert.Contains("config/acceptance-manifest.json, docs/widget.md", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_Implementer_RejectsExactlyTouchedAuthoredPaths(bool touched)
    {
        var result = Evaluate(CheckpointKind.Implementer, [], touched ? [TestPath] : []);
        Assert.Equal(touched ? ScopeOutcome.Failed : ScopeOutcome.Passed, result.Outcome);
        Assert.Equal(touched ? [TestPath] : Array.Empty<string>(), result.OffendingPaths);
        if (touched) Assert.Contains(TestPath, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_Author_FailsClosedForBinarySourceAndCaseVariants()
    {
        var result = Evaluate(CheckpointKind.Author,
            [(null, null, SourcePath), (null, null, TestPath), (1, 0, "Tests/Upper.cs"), (1, 0, "src-other/Widget.cs")], []);
        Assert.Equal(ScopeOutcome.Failed, result.Outcome);
        Assert.Equal(["Tests/Upper.cs", "src-other/Widget.cs", SourcePath], result.OffendingPaths);
    }

    [Fact]
    public void SelectCheckpoint_IdentifiesBothCompletedDeveloperScopes()
    {
        var (kernel, goal) = CreateGoal();
        Assert.True(IsTestFirst(goal));
        Assert.Null(SelectCheckpoint(goal));
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks[0]);
        Assert.Null(SelectCheckpoint(goal));
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks[1]);
        var author = SelectCheckpoint(goal);
        Assert.NotNull(author);
        Assert.Equal(goal.Tasks[1].Id, author.Value.Developer.Id);
        Assert.Equal(CheckpointKind.Author, author.Value.Kind);
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks[2]);
        var implementer = SelectCheckpoint(goal);
        Assert.NotNull(implementer);
        Assert.Equal(goal.Tasks[2].Id, implementer.Value.Developer.Id);
        Assert.Equal(CheckpointKind.Implementer, implementer.Value.Kind);
    }

    [Fact]
    public void IsTestFirst_UsesOnlyLastIntakeDecisionAndDoesNotInferFromRoles()
    {
        var (kernel, goal) = CreateGoal();
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks[0]);
        ConductorDriverTests.PassVerification(kernel, goal, goal.Tasks[1]);
        kernel.RecordGoalPolicyDecision(goal.Id, "Intake pipeline decision (override): scout; latest decision.");
        Assert.False(IsTestFirst(goal));
        Assert.Null(SelectCheckpoint(goal));
        kernel.RecordGoalPolicyDecision(goal.Id, "Intake pipeline decision (override): test-first; restored.");
        kernel.RecordGoalPolicyDecision(goal.Id, "Unrelated policy decision.");
        Assert.True(IsTestFirst(goal));
    }

    [Fact]
    public void IsRepeat_IsPerTaskAndPersistsAcrossLaterPassedNotes()
    {
        var (kernel, goal) = CreateGoal();
        var author = goal.Tasks[1];
        Assert.False(IsRepeat(goal, author.Id));
        kernel.RecordTaskNote(goal.Id, author.Id, "test-first scope check failed: docs/widget.md");
        kernel.RecordTaskNote(goal.Id, author.Id, "test-first scope check passed: clean");
        Assert.True(IsRepeat(goal, author.Id));
        Assert.False(IsRepeat(goal, goal.Tasks[2].Id));
    }

    [Fact]
    public void Read_AcrossTrunkMerge_ReportsOnlyAuthorChangesAndNoImplementerTestTouches()
    {
        using var repository = new ScopeRepository();
        repository.AuthorAndMergeTrunk();
        repository.CommitSourceImplementation();

        var result = Read(repository.Root, "main", "author-1");
        Assert.Equal(ScopeOutcome.Passed, result.Outcome);
        Assert.Equal([SourcePath, TestPath], result.AuthorChanges.Select(row => row.Path).Order(StringComparer.Ordinal));
        Assert.All(result.AuthorChanges, row => Assert.Equal(0, row.Removed));
        Assert.All(result.AuthorChanges, row => Assert.True(row.Added > 0));
        Assert.Empty(result.ImplementerTouchedAuthoredPaths);
    }

    [Fact]
    public void Read_ImplementerEditingAuthoredTest_ReportsThatPath()
    {
        using var repository = new ScopeRepository();
        repository.AuthorAndMergeTrunk();
        repository.CommitSourceImplementation();
        repository.Write(TestPath, "changed test\n");
        repository.Commit("Change authored test", "impl-1");

        var result = Read(repository.Root, "main", "author-1");
        Assert.Equal(ScopeOutcome.Passed, result.Outcome);
        Assert.Equal([TestPath], result.ImplementerTouchedAuthoredPaths);
        Assert.Equal(ScopeOutcome.Failed,
            Evaluate(CheckpointKind.Implementer, result.AuthorChanges, result.ImplementerTouchedAuthoredPaths).Outcome);
    }

    [Fact]
    public void Read_NoExactAuthorTrailer_ReturnsSkipReason()
    {
        using var repository = new ScopeRepository();
        repository.Write(TestPath, "test\n");
        repository.Commit("Task-Id: author-1 is body text, not a trailer\n\nOrdinary body.", "author-10");

        var result = Read(repository.Root, "main", "author-1");
        Assert.Equal(ScopeOutcome.Skipped, result.Outcome);
        Assert.Contains("no commit with Task-Id trailer author-1", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_NewestAuthorCommitWins()
    {
        using var repository = new ScopeRepository();
        repository.AuthorAndMergeTrunk("author-1");
        repository.Write("tests/Second.cs", "second authored test\n");
        repository.Commit("Second author round", "author-1");
        repository.CommitSourceImplementation();

        var result = Read(repository.Root, "main", "author-1");
        Assert.Equal(ScopeOutcome.Passed, result.Outcome);
        Assert.Equal([SourcePath, TestPath, "tests/Second.cs"],
            result.AuthorChanges.Select(row => row.Path).Order(StringComparer.Ordinal));
        Assert.Empty(result.ImplementerTouchedAuthoredPaths);
    }

    [Fact]
    public void Read_MergeTrailerDoesNotAttributeTrunkChangesToAuthor()
    {
        using var repository = new ScopeRepository();
        repository.AuthorAndMergeTrunk("author-1");

        var result = Read(repository.Root, "main", "author-1");
        Assert.Equal(ScopeOutcome.Passed, result.Outcome);
        Assert.Equal([SourcePath, TestPath], result.AuthorChanges.Select(row => row.Path).Order(StringComparer.Ordinal));
        Assert.All(result.AuthorChanges, row => Assert.Equal(0, row.Removed));
        Assert.Empty(result.ImplementerTouchedAuthoredPaths);
    }

    [Fact]
    public void Read_MissingIntegrationBranch_SkipsWithFailingStep()
    {
        using var repository = new ScopeRepository();
        var result = Read(repository.Root, "missing-branch", "author-1");
        Assert.Equal(ScopeOutcome.Skipped, result.Outcome);
        Assert.StartsWith("author commit log:", result.Message, StringComparison.Ordinal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, AgentCatalog.Default().Agents,
            GoalObjectivePlanner.Build("Implement src/Widget.cs with focused tests.", GoalIntakePipeline.TestFirst));
        return (kernel, goal);
    }

    private sealed class ScopeRepository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-test-first", Guid.NewGuid().ToString("N"));

        internal ScopeRepository()
        {
            Directory.CreateDirectory(Root);
            Git("init", "-b", "main");
            Git("config", "user.email", "tests@example.invalid");
            Git("config", "user.name", "Tests");
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Write(SourcePath, "seed\n");
            Commit("Seed", "seed");
            Git("checkout", "-b", "topic");
        }

        internal void AuthorAndMergeTrunk(string mergeTaskId = "merge-task")
        {
            Write(TestPath, "authored test\n");
            File.AppendAllText(Path.Combine(Root, SourcePath), "stub\n");
            Commit("Author tests", "author-1");
            Git("checkout", "main");
            Write("trunk.txt", "unrelated trunk change\n");
            Commit("Trunk change", "trunk-task");
            Git("checkout", "topic");
            Git("merge", "--no-ff", "main", "-m", "Merge trunk", "-m", $"Task-Id: {mergeTaskId}");
        }

        internal void CommitSourceImplementation()
        {
            Write(SourcePath, "implemented behavior\n");
            Commit("Implement", "impl-1");
        }

        internal void Write(string path, string content)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        internal void Commit(string message, string taskId)
        {
            Git("add", "--all");
            Git("commit", "-m", message, "-m", $"Task-Id: {taskId}");
        }

        private void Git(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded && !result.DrainTimedOut, $"git {string.Join(' ', args)}: {result.Error}");
        }

        public void Dispose()
        {
            foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
