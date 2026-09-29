using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsDeveloperCompletionStructuralPreflight
{
    [Xunit.Fact]
    public void RatchetViolation_RetriesDeveloperWithoutPreparingTesterDispatch()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Developer completion structural preflight");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            Directory.CreateDirectory(worktreePath);
            WriteSourceSizeAuthority(worktreePath, maximumLineCount: 2, actualLineCount: 3);
            var retries = new List<(TaskId TaskId, string Message, RetryRoundKind? RoundKind, RetryCause Cause)>();
            var dispatchedRoles = new List<AgentRole>();
            var notes = new List<string>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    dispatchedRoles.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retries.Add((taskId, message, roundKind, cause));
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordTaskNote: (_, taskId, message) =>
                {
                    Assert.Equal(developer.Id, taskId);
                    notes.Add(message);
                },
                executionDirectory: executionDirectory);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var retry = Assert.Single(retries);
            Assert.Equal(developer.Id, retry.TaskId);
            Assert.Equal(RetryRoundKind.Mechanical, retry.RoundKind);
            Assert.Equal(RetryCause.NewSourceFinding, retry.Cause);
            Assert.Contains("guarded.cs", retry.Message, StringComparison.Ordinal);
            Assert.Contains("3 lines", retry.Message, StringComparison.Ordinal);
            Assert.Contains("ceiling of 2", retry.Message, StringComparison.Ordinal);
            Assert.Equal([AgentRole.Developer], dispatchedRoles);
            Assert.StartsWith("developer-completion structural pre-check failed:", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void CleanCandidate_RecordsPassedPrecheckAndPreparesTesterDispatch()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Clean Developer completion structural preflight");
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            Directory.CreateDirectory(worktreePath);
            WriteSourceSizeAuthority(worktreePath, maximumLineCount: 3, actualLineCount: 3);
            WriteAcceptanceManifest(worktreePath, duplicateLane: false);
            var dispatchedRoles = new List<AgentRole>();
            var notes = new List<string>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    dispatchedRoles.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                recordTaskNote: (_, _, message) => notes.Add(message),
                executionDirectory: executionDirectory);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal([AgentRole.Tester], dispatchedRoles);
            Assert.StartsWith(
                "developer-completion structural pre-check passed:",
                Assert.Single(notes),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ManifestLaneViolation_RetriesDeveloperWithoutPreparingTesterDispatch()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Manifest Developer completion structural preflight");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
            Directory.CreateDirectory(worktreePath);
            WriteSourceSizeAuthority(worktreePath, maximumLineCount: 3, actualLineCount: 3);
            WriteAcceptanceManifest(worktreePath, duplicateLane: true);
            var retryMessages = new List<string>();
            var dispatchedRoles = new List<AgentRole>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    dispatchedRoles.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    Assert.Equal(developer.Id, taskId);
                    Assert.Equal(RetryRoundKind.Mechanical, roundKind);
                    Assert.Equal(RetryCause.NewSourceFinding, cause);
                    retryMessages.Add(message);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                executionDirectory: executionDirectory);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var retryMessage = Assert.Single(retryMessages);
            Assert.Contains("config/acceptance-manifest.json", retryMessage, StringComparison.Ordinal);
            Assert.Contains("lane-a", retryMessage, StringComparison.Ordinal);
            Assert.Contains("duplicated", retryMessage, StringComparison.Ordinal);
            Assert.Equal([AgentRole.Developer], dispatchedRoles);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void HungPrecheck_IsAbandonedAfterBoundAndTesterDispatchContinues()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var returned = 0;
        try
        {
            var (kernel, goal) = SoftwareGoal("Bounded Developer completion structural preflight");
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
            {
                PassVerification(kernel, goal, task);
            }

            Directory.CreateDirectory(GoalWorktrees.WorktreePath(executionDirectory, goal.Id));
            var dispatchedRoles = new List<AgentRole>();
            var notes = new List<string>();
            var retries = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: candidate =>
                {
                    dispatchedRoles.Add(candidate.Tasks.First(task => task.Status == WorkTaskStatus.Assigned).RequiredRole);
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (_, _, _, _, _) =>
                {
                    retries++;
                    throw new InvalidOperationException("A skipped pre-check must not retry the Developer.");
                },
                recordTaskNote: (_, _, message) => notes.Add(message),
                executionDirectory: executionDirectory);
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ =>
                {
                    entered.Set();
                    release.Wait();
                    Volatile.Write(ref returned, 1);
                    return new DeveloperCompletionStructuralFindings(false, "released");
                },
                TimeSpan.FromMilliseconds(100));

            var tick = Task.Factory.StartNew(
                () => driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);

            Assert.True(tick.Wait(TestHangGuard.Bound), "AdvanceOnce did not return while the pre-check stayed blocked.");
            Assert.True(entered.Wait(TestHangGuard.Bound), "Developer completion pre-check was not entered.");
            Assert.Equal(0, Volatile.Read(ref returned));
            Assert.False(release.IsSet);
            Assert.Equal(0, retries);
            Assert.Equal([AgentRole.Tester], dispatchedRoles);
            var note = Assert.Single(notes);
            Assert.StartsWith("developer-completion structural pre-check skipped:", note, StringComparison.Ordinal);
            Assert.Contains("100 ms bound", note, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    private static void WriteSourceSizeAuthority(
        string root,
        int maximumLineCount,
        int actualLineCount)
    {
        var authorityPath = Path.Combine(
            root,
            SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
        File.WriteAllText(
            authorityPath,
            $"new SourceSizeCeiling(\"guarded.cs\", {maximumLineCount})");
        File.WriteAllLines(
            Path.Combine(root, "guarded.cs"),
            Enumerable.Repeat("line", actualLineCount));
    }

    private static void WriteAcceptanceManifest(string root, bool duplicateLane)
    {
        var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var lanes = duplicateLane
            ? new[]
            {
                new { name = "lane-a", filter = "FullyQualifiedName~A" },
                new { name = "lane-a", filter = "FullyQualifiedName~B" }
            }
            : [new { name = "lane-a", filter = "FullyQualifiedName~A" }];
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new { engine = new { infrastructureTestLanes = lanes } }));
    }
}
