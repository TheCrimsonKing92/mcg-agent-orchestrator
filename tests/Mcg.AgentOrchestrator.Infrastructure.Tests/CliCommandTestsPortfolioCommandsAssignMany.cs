using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its workspace, databases, ids file and console capture.
public sealed class CliCommandTestsPortfolioCommandsAssignMany : CliCommandTestBase
{
    private const string GoalIdValue = "aaaaaaaa111111111111111111111111";
    private const string UnknownId = "not-a-member";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PositionalOrFileIds_PrintPerIdResultsAndAssignKnownMembers(bool fromFile)
    {
        var root = CreateTempDirectory();
        try
        {
            var (workspace, store, epic, backlog) = Seed(root);
            var args = new List<string> { "epic-assign-many", "Board" };
            if (fromFile)
            {
                var path = Path.Combine(root, "ids.txt");
                File.WriteAllLines(path, ["  aaaaaaaa  ", "", backlog.Id[..8], UnknownId]);
                args.AddRange(["--ids-file", path]);
            }
            else
                args.AddRange(["aaaaaaaa", backlog.Id[..8], UnknownId]);

            var output = ExecuteCliAndCapture(args, new AgentOrchestratorKernel(), workspace);

            Xunit.Assert.Equal(ExpectedOutput(epic, backlog), output);
            Xunit.Assert.Equal(epic.Id, store.GetGoalMembershipAsync(GoalIdValue).GetAwaiter().GetResult()!.EpicId);
            Xunit.Assert.Equal(epic.Id, store.GetBacklogMembershipAsync(backlog.Id).GetAwaiter().GetResult()!.EpicId);
            Xunit.Assert.Equal(2, store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult().Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void DryRunAndUnionedFileIds_PreviewSameResultsWithoutMembershipChanges()
    {
        var root = CreateTempDirectory();
        try
        {
            var (workspace, store, epic, backlog) = Seed(root);
            var path = Path.Combine(root, "ids.txt");
            File.WriteAllLines(path, ["AAAAAAAA", backlog.Id[..8], "", UnknownId, UnknownId]);
            var args = new[] { "epic-assign-many", epic.Id[..8], "aaaaaaaa", "--ids-file", path };
            var preview = ExecuteCliAndCapture([..args, "--dry-run"], new AgentOrchestratorKernel(), workspace);

            Xunit.Assert.Equal(ExpectedOutput(epic, backlog, dryRun: true), preview);
            Xunit.Assert.Null(store.GetGoalMembershipAsync(GoalIdValue).GetAwaiter().GetResult());
            Xunit.Assert.Null(store.GetBacklogMembershipAsync(backlog.Id).GetAwaiter().GetResult());
            Xunit.Assert.Empty(store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult());

            var committed = ExecuteCliAndCapture(args, new AgentOrchestratorKernel(), workspace);
            Xunit.Assert.Equal(ExpectedOutput(epic, backlog), committed);
            Xunit.Assert.Equal(committed, preview.Replace(" (dry-run; no memberships changed)", "", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void AmbiguousAndBothMatchIds_ReportExactErrorsAndAssignOtherIds()
    {
        var root = CreateTempDirectory();
        try
        {
            var (workspace, store, epic, backlog) = Seed(root);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId(GoalIdValue), "Known");
            kernel.CreateGoal(new GoalId("bbbbbbbb111111111111111111111111"), "Ambiguous one");
            kernel.CreateGoal(new GoalId("bbbbbbbb222222222222222222222222"), "Ambiguous two");
            kernel.CreateGoal(new GoalId(backlog.Id), "Both match");
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();

            var output = ExecuteCliAndCapture(
                ["epic-assign-many", "Board", "bbbbbbbb", backlog.Id[..8], "aaaaaaaa"],
                new AgentOrchestratorKernel(), workspace);

            Xunit.Assert.Equal(string.Join(Environment.NewLine,
            [
                "bbbbbbbb: ambiguous - Goal prefix 'bbbbbbbb' is ambiguous.",
                $"{backlog.Id[..8]}: ambiguous - Portfolio assignment target '{backlog.Id[..8]}' matches both a goal and a backlog item.",
                "goal aaaaaaaa: assigned",
                $"epic-assign-many {epic.Id[..8]}: assigned=1 already-member=0 moved=0 unknown=0 ambiguous=2",
                ""
            ]), output);
            Xunit.Assert.Equal(epic.Id, store.GetGoalMembershipAsync(GoalIdValue).GetAwaiter().GetResult()!.EpicId);
            Xunit.Assert.Null(store.GetGoalMembershipAsync(backlog.Id).GetAwaiter().GetResult());
            Xunit.Assert.Null(store.GetBacklogMembershipAsync(backlog.Id).GetAwaiter().GetResult());
            Xunit.Assert.Single(store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void MissingIdsFile_FailsWithPathBeforeAnyAssignment()
    {
        var root = CreateTempDirectory();
        try
        {
            var (workspace, store, epic, _) = Seed(root);
            var path = Path.Combine(root, "missing.txt");
            var error = Xunit.Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
                ["epic-assign-many", "Board", "aaaaaaaa", "--ids-file", path],
                new AgentOrchestratorKernel(), workspace));
            Xunit.Assert.Contains(path, error.Message);
            Xunit.Assert.Empty(store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void PopulatedCallerKernel_StillResolvesPersistedMetadataOnly()
    {
        var root = CreateTempDirectory();
        try
        {
            var (workspace, store, epic, _) = Seed(root);
            var callerKernel = new AgentOrchestratorKernel();
            const string unpersistedId = "bbbbbbbb222222222222222222222222";
            callerKernel.CreateGoal(new GoalId(unpersistedId), "Not persisted");
            var output = ExecuteCliAndCapture(
                ["epic-assign-many", "Board", "aaaaaaaa", "bbbbbbbb"], callerKernel, workspace);
            Xunit.Assert.Equal(string.Join(Environment.NewLine,
            [
                "goal aaaaaaaa: assigned", "bbbbbbbb: unknown",
                $"epic-assign-many {epic.Id[..8]}: assigned=1 already-member=0 moved=0 unknown=1 ambiguous=0", ""
            ]), output);
            Xunit.Assert.Equal(epic.Id, store.GetGoalMembershipAsync(GoalIdValue).GetAwaiter().GetResult()!.EpicId);
            Xunit.Assert.Null(store.GetGoalMembershipAsync(unpersistedId).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("--ids-file")]
    [Xunit.InlineData("--ids-file=")]
    [Xunit.InlineData("--dry-run")]
    public void NoIdsOrMissingFilePath_FailsBeforeAnyAssignment(string option)
    {
        var root = CreateTempDirectory();
        try
        {
            var (workspace, store, epic, _) = Seed(root);
            Xunit.Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
                ["epic-assign-many", "Board", option], new AgentOrchestratorKernel(), workspace));
            Xunit.Assert.Empty(store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void HelpAndInvalidFlags_RegisterNewVerbWithoutResolvingGoals()
    {
        AssertHelpCommandDoesNotResolveGoal(["epic-assign-many", "--help"], CliCommandHelp.EpicAssignManyUsage);
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            Xunit.Assert.Contains("epic-assign-many", CliArgumentParser.RecognizedCommands);
            Xunit.Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
                ["epic-assign-many", "Board", "aaaaaaaa", "--unknown"], new AgentOrchestratorKernel(), workspace));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string ExpectedOutput(PortfolioEpic epic, BacklogItem backlog, bool dryRun = false) =>
        string.Join(Environment.NewLine,
        [
            "goal aaaaaaaa: assigned",
            $"backlog {backlog.Id[..8]}: assigned",
            $"{UnknownId}: unknown",
            $"epic-assign-many {epic.Id[..8]}: assigned=2 already-member=0 moved=0 unknown=1 ambiguous=0"
                + (dryRun ? " (dry-run; no memberships changed)" : ""),
            ""
        ]);

    private static (OrchestratorWorkspace, PortfolioStore, PortfolioEpic, BacklogItem) Seed(string root)
    {
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(GoalIdValue), "Persisted assignment goal");
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        var backlog = new BacklogStore(workspace.BacklogStorePath).AddAsync("Batch backlog").GetAwaiter().GetResult();
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Board").GetAwaiter().GetResult();
        return (workspace, store, epic, backlog);
    }
}
