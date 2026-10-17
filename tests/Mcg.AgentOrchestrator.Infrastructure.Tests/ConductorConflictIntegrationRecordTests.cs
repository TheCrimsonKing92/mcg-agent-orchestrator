using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;

// Parallel-safe: every case owns its kernel, repository, worktree and journal.
public sealed class ConductorConflictIntegrationRecordTests
{
    internal sealed class Fixture : IDisposable
    {
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal TaskSpec Developer => Goal.Tasks.First();
        internal TaskSpec Tester => Goal.Tasks.Last();
        internal AdditiveConflictGitFixture GitFixture { get; }
        internal ConductorConflictIntegration Coordinator { get; }
        internal int RetryCalls { get; private set; }
        internal int InnerCalls { get; private set; }
        internal int BuildCalls { get; private set; }
        internal string? Note { get; private set; }
        internal DeveloperBranchIntegrationResult? InnerResult { get; private set; }
        internal GoalOperationJournalEntry Entry => Assert.Single(GoalOperationJournal.Read(GitFixture.Repository, Goal.Id).LatestByOperation,
            entry => entry.Operation == ConductorConflictIntegration.OperationName);

        internal Fixture(bool tester = true, IReadOnlyList<ConflictFile>? files = null,
            Func<string, string?>? objectiveReader = null, bool failBuild = false)
        {
            Goal = Kernel.CreateGoal("Keep the goal behavior", tester
                ? [new(TaskId.New(), "Implement", AgentRole.Developer), new(TaskId.New(), "Verify", AgentRole.Tester)]
                : [new(TaskId.New(), "Implement", AgentRole.Developer)]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            PassVerification(Kernel, Goal, Developer);
            GitFixture = new(files ?? [new(WidgetPath, "base\n", "main\n", "goal\n")], Goal.Id,
                mainCommitMessage: "Integrate goal/aaaaaaaa\n\nGoal: Landing A");
            var integration = new RatchetConflictDispatchIntegration(Kernel, GitFixture.Repository, "main",
                Path.Combine(GitFixture.Repository, ".orchestrator", "conduct-events.log"));
            Coordinator = new(Kernel, GitFixture.Repository, "main", Retry, goal =>
            {
                InnerCalls++;
                return InnerResult = integration.IntegrateMainBeforeDeveloperDispatch(goal);
            }, path =>
            {
                Assert.Equal(GitFixture.Worktree, path);
                BuildCalls++;
                return new(true, failBuild ? 1 : 0, failBuild ? "error CS0103: missing symbol" : "build passed");
            }, objectiveReader ?? (_ => null));
        }

        internal TaskSpec Retry(GoalId goalId, TaskId taskId, string note, RetryRoundKind? kind, RetryCause cause)
        {
            RetryCalls++;
            Note = note;
            Assert.Equal(RetryCause.MainDriftConflict, cause);
            Assert.Equal(RetryRoundKind.Standard, kind);
            return Kernel.RetryTaskAutomatically(goalId, taskId, note, cause, retryRoundKind: kind, authoritativeRetryFeedback: true);
        }

        internal void Admit()
        {
            Assert.True(Coordinator.TryRecord(Goal).Started);
            Assert.Equal(DeveloperBranchIntegrationStatus.ConflictAdmitted, Coordinator.IntegrateMainBeforeDeveloperDispatch(Goal).Status);
        }

        internal string Resolve(bool outside = false, bool marker = false)
        {
            File.WriteAllText(Path.Combine(GitFixture.Worktree, WidgetPath), marker ? "main\ngoal\n>>>>>>> leftover\n" : "main\ngoal\n");
            var paths = new List<string> { WidgetPath };
            if (outside)
            {
                File.WriteAllText(Path.Combine(GitFixture.Worktree, "src/Other.cs"), "outside\n");
                paths.Add("src/Other.cs");
            }
            var committed = new DispatchWorktreeCommitter().TryCommitWorktreeEdits(GitFixture.Worktree, "Resolve conductor integration", paths);
            Assert.True(committed.Succeeded, committed.ToString());
            return Git(GitFixture.Worktree, "rev-parse", "HEAD").Trim();
        }

        internal void RewriteRound(Func<JsonElement, object> rewrite, bool failed = false)
        {
            using var json = JsonDocument.Parse(Entry.Detail!);
            var detail = JsonSerializer.Serialize(rewrite(json.RootElement));
            if (failed) GoalOperationJournal.Failed(GitFixture.Repository, Goal, ConductorConflictIntegration.OperationName, detail);
            else GoalOperationJournal.Begin(GitFixture.Repository, Goal, ConductorConflictIntegration.OperationName, detail);
        }
        public void Dispose() => GitFixture.Dispose();
    }

    [Fact]
    public void TryRecord_Conflict_RecordsOneRoundAndRetriesWithoutGitChanges()
    {
        using var fixture = new Fixture(objectiveReader: prefix => prefix == "aaaaaaaa" ? "Landing objective" : null);
        var result = fixture.Coordinator.TryRecord(fixture.Goal);
        Assert.True(result.Started, result.Detail);
        fixture.GitFixture.AssertRestored();
        Assert.Equal(1, fixture.RetryCalls);
        Assert.Equal(GoalOperationStatus.Begin, fixture.Entry.Status);
        using var record = JsonDocument.Parse(fixture.Entry.Detail!);
        var root = record.RootElement;
        Assert.Equal(fixture.GitFixture.OriginalHead, root.GetProperty("OriginalHead").GetString());
        Assert.Equal(fixture.GitFixture.MainHead, root.GetProperty("MainSha").GetString());
        Assert.Equal(new[] { WidgetPath }, root.GetProperty("ConflictPaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(fixture.Developer.Id.Value, root.GetProperty("DeveloperTaskId").GetString());
        Assert.Equal(fixture.Developer.LatestRetryAt, root.GetProperty("RetryAt").GetDateTimeOffset());
        Assert.Equal(new[] { "aaaaaaaa" }, root.GetProperty("LandingGoalPrefixes").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(WidgetPath, fixture.Note);
        Assert.Contains("Landing objective", fixture.Note);
        Assert.Contains("+main", fixture.Note);
        Assert.EndsWith(ClosingProcedure, fixture.Note);
    }

    internal const string ClosingProcedure = "The conductor has started merging main into this branch; the files below are left with diff3 conflict markers: src/Widget.cs. Edit only these files. In each conflict block, write code that keeps the behavior this goal's brief needs and the behavior the landing goal above added. Before finishing, open each listed file and confirm no line starts with <<<<<<<, |||||||, ======= or >>>>>>>. Leave the merge for the commit to complete. If the result changes any other file, leaves a marker, or fails the build check, the conductor resets the branch and escalates.";

    [Fact]
    public void TryRecord_MissingObjective_UsesLandingGoalLine()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Coordinator.TryRecord(fixture.Goal).Started);
        Assert.Contains("Landing goal aaaaaaaa objective: Landing A", fixture.Note);
    }

    [Fact]
    public void TryRecord_LargeLandingDiff_CapsAtTwoHundredLines()
    {
        // Five header lines, one removed line and 244 added lines make a 250-line diff.
        var main = string.Concat(Enumerable.Range(0, 244).Select(index => $"main {index}\n"));
        using var fixture = new Fixture(files: [new(WidgetPath, "base\n", main, "goal\n")]);
        var diff = Git(fixture.GitFixture.Worktree, "diff", fixture.GitFixture.MainHead + "^", fixture.GitFixture.MainHead, "--", WidgetPath)
            .ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal(250, diff.Length);
        Assert.True(fixture.Coordinator.TryRecord(fixture.Goal).Started);
        Assert.Contains("50 more lines omitted", fixture.Note);
        Assert.Contains(diff[199], fixture.Note);
        Assert.DoesNotContain(diff[200] + "\n", fixture.Note!.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("repeat", "repeat conflict on unchanged candidate")]
    [InlineData("dirty", "dirty worktree")]
    [InlineData("no tester", "no Tester task")]
    [InlineData("detached", "detached")]
    public void TryRecord_IneligibleCandidate_RefusesWithoutRetry(string kind, string reason)
    {
        using var fixture = new Fixture(tester: kind != "no tester");
        if (kind == "repeat") Assert.True(fixture.Coordinator.TryRecord(fixture.Goal).Started);
        if (kind == "dirty") File.WriteAllText(Path.Combine(fixture.GitFixture.Worktree, WidgetPath), "dirty\n");
        if (kind == "detached") Git(fixture.GitFixture.Worktree, "checkout", "--detach");
        var retries = fixture.RetryCalls;
        var status = Git(fixture.GitFixture.Worktree, "status", "--porcelain=v1");
        var result = fixture.Coordinator.TryRecord(fixture.Goal);
        Assert.False(result.Started);
        Assert.Contains(reason, result.Detail);
        Assert.Equal(retries, fixture.RetryCalls);
        Assert.Equal(fixture.GitFixture.OriginalHead, Git(fixture.GitFixture.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(status, Git(fixture.GitFixture.Worktree, "status", "--porcelain=v1"));
        Assert.Equal(1, GitCli.Run(fixture.GitFixture.Worktree, "rev-parse", "--verify", "--quiet", "MERGE_HEAD").ExitCode);
    }

    [Fact]
    public void TryRecord_CleanMerge_RebindsBothAcceptanceObligations()
    {
        using var fixture = new Fixture(files: [new(WidgetPath, "base\n", "base\n", "goal\n"), new("src/Other.cs", "base\n", "main\n", "base\n")]);
        fixture.Kernel.RecordGoalRefinement(fixture.Goal.Id, new RefinedSpec(fixture.Goal.Objective, ["one", "two"], VerificationClass.TestVerifiable, [], []));
        for (var index = 0; index < 2; index++)
            fixture.Kernel.MapCriterionEvidenceOwner(fixture.Goal.Id, index, 1, CriterionEvidenceOwner.Acceptance, "seed",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: fixture.GitFixture.OriginalHead);
        var result = fixture.Coordinator.TryRecord(fixture.Goal);
        Assert.True(result.Started);
        Assert.Equal("merged main cleanly", result.Detail);
        var head = Git(fixture.GitFixture.Worktree, "rev-parse", "HEAD").Trim();
        Assert.Equal(new[] { head, fixture.GitFixture.OriginalHead, fixture.GitFixture.MainHead },
            Git(fixture.GitFixture.Worktree, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' '));
        Assert.Equal(2, fixture.Goal.CriterionEvidenceObligations.Count);
        Assert.All(fixture.Goal.CriterionEvidenceObligations, item => Assert.Equal(head, item.ExpectedCandidateSha));
        Assert.Equal(0, fixture.RetryCalls);
        Assert.DoesNotContain(GoalOperationJournal.Read(fixture.GitFixture.Repository, fixture.Goal.Id).Entries, entry => entry.Operation == ConductorConflictIntegration.OperationName);
    }
}
