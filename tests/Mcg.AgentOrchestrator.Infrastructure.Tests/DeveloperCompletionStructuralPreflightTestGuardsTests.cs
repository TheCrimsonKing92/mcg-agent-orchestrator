using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DeveloperCompletionStructuralPreflightTestGuardsTests
{
    private const string PassMessage = "source size ratchet and acceptance manifest lane consistency checks passed";
    private const string MisfiledClass = "ConductorBatchLoopTestsMisfiledFixture";
    private const string TestDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
    private const string FixturePath = TestDirectory + "/GuardFixture.cs";
    private const string PartitionManifest = """
        { "version": 1, "engine": { "infrastructureTestLanes": [
          { "name": "Dotnet build slots", "filter": "FullyQualifiedName~ConductorBatchLoopTests", "ownedCollections": ["DotnetBuildSlots"] },
          { "name": "Remainder balance B", "filter": "FullyQualifiedName~RunGoalServiceTests" },
          { "name": "Remainder", "filter": "FullyQualifiedName!~ConductorBatchLoopTests&FullyQualifiedName!~RunGoalService" }
        ] } }
        """;

    [Theory]
    [InlineData(MisfiledClass, false, "isolated-collection")]
    [InlineData("ConductorLegacyHolderAdmissionFixture", true, "lane-membership")]
    public void Misfiled_class_reports_source_class_and_rule(string name, bool isolated, string rule)
    {
        using var fixture = new Fixture();
        fixture.WriteClass(name, isolated);

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.True(result.HasViolation);
        Assert.Contains(FixturePath, result.Message, StringComparison.Ordinal);
        Assert.Contains(name, result.Message, StringComparison.Ordinal);
        Assert.Contains($"[{rule}]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Correct_collection_keeps_unchanged_pass_message()
    {
        using var fixture = new Fixture();
        fixture.WriteClass("ConductorBatchLoopTestsCorrectFixture", isolated: true);

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.False(result.HasViolation, result.Message);
        Assert.Equal(PassMessage, result.Message);
    }

    [Theory]
    [InlineData(MisfiledClass, false, "isolated-collection")]
    [InlineData("ConductorLegacyHolderAdmissionFixture", true, "lane-membership")]
    public void Inherited_class_reports_declaring_file_instead_of_earlier_base_file(string name, bool isolated, string rule)
    {
        using var fixture = new Fixture();
        const string basePath = TestDirectory + "/ConductorBatchLoopTests.cs";
        File.WriteAllText(Path.Combine(fixture.Root, basePath),
            "public abstract " + "class ConductorBatchLoopTests { }");
        fixture.WriteClass(name, isolated, baseName: "ConductorBatchLoopTests");
        var source = Assert.Single(AcceptanceTestClassSourceScanner.ScanSources(fixture.Root)
            .Where(item => item.FullName == name));
        Assert.Equal(new[] { basePath, FixturePath }, source.SourcePaths);

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.True(result.HasViolation);
        Assert.Contains(FixturePath, result.Message, StringComparison.Ordinal);
        Assert.Contains(name, result.Message, StringComparison.Ordinal);
        Assert.Contains($"[{rule}]", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(basePath, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_exemption_keeps_unchanged_pass_message()
    {
        using var fixture = new Fixture();
        fixture.WriteClass(MisfiledClass, isolated: false);
        var exemptions = TestCollectionGuardRules.LoadIsolatedCollectionExemptions(fixture.Root)
            .Select(item => new { @class = item.Key, reason = item.Value })
            .Append(new { @class = MisfiledClass, reason = "Fixture uses no build or slot acquisition." });
        File.WriteAllText(fixture.ExemptionsPath, JsonSerializer.Serialize(exemptions));

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.False(result.HasViolation, result.Message);
        Assert.Equal(PassMessage, result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_or_invalid_exemptions_fail_and_keep_membership_feedback(bool missing)
    {
        using var fixture = new Fixture();
        fixture.WriteClass("ConductorLegacyHolderAdmissionFixture", isolated: true);
        if (missing) File.Delete(fixture.ExemptionsPath);
        else File.WriteAllText(fixture.ExemptionsPath, "invalid json");

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.True(result.HasViolation);
        Assert.Contains(TestCollectionGuardRules.ExemptionsRelativePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("[isolated-collection]", result.Message, StringComparison.Ordinal);
        Assert.Contains(FixturePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("ConductorLegacyHolderAdmissionFixture [lane-membership]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_exemptions_are_a_violation_instead_of_a_skipped_precheck()
    {
        using var fixture = new Fixture();
        fixture.WriteClass("ConductorBatchLoopTestsCorrectFixture", isolated: true);
        using var lockedFile = File.Open(fixture.ExemptionsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.True(result.HasViolation);
        Assert.Contains(TestCollectionGuardRules.ExemptionsRelativePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("[isolated-collection]", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_test_project_or_lane_skips_both_rules(bool missingTestProject)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.ExemptionsPath);
        if (missingTestProject)
            Directory.Delete(Path.Combine(fixture.Root, TestDirectory), recursive: true);
        else
        {
            fixture.WriteClass("ConductorLegacyHolderAdmissionFixture", isolated: true);
            File.WriteAllText(Path.Combine(fixture.Root, "config/acceptance-manifest.json"),
                JsonSerializer.Serialize(new { engine = new { infrastructureTestLanes = new[]
                {
                    new { name = "another lane", filter = "FullyQualifiedName~ConductorBatchLoopTests" }
                } } }));
        }

        var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

        Assert.False(result.HasViolation, result.Message);
        Assert.Equal(PassMessage, result.Message);
    }

    [Fact]
    public void Guard_violation_retries_only_developer_without_preparing_tester_dispatch()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Test collection structural preflight");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
                PassVerification(kernel, goal, task);
            using var fixture = new Fixture(GoalWorktrees.WorktreePath(executionDirectory, goal.Id));
            fixture.WriteClass(MisfiledClass, isolated: false);
            var retries = new List<(TaskId TaskId, string Message, RetryCause Cause)>();
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
                    retries.Add((taskId, message, cause));
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordTaskNote: (_, _, message) => notes.Add(message),
                executionDirectory: executionDirectory);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var retry = Assert.Single(retries);
            Assert.Equal(developer.Id, retry.TaskId);
            Assert.Equal(RetryCause.NewSourceFinding, retry.Cause);
            Assert.Contains(MisfiledClass, retry.Message, StringComparison.Ordinal);
            Assert.Contains("isolated-collection", retry.Message, StringComparison.Ordinal);
            Assert.Equal([AgentRole.Developer], dispatchedRoles);
            Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
            Assert.Null(tester.LastDispatch);
            Assert.StartsWith("developer-completion structural pre-check failed:", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    [Fact]
    public void Unassigned_class_retries_developer_before_tester_dispatch()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Lane partition structural preflight");
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
                PassVerification(kernel, goal, task);
            using var fixture = new Fixture(GoalWorktrees.WorktreePath(executionDirectory, goal.Id));
            File.WriteAllText(Path.Combine(fixture.Root, "config/acceptance-manifest.json"), PartitionManifest);
            fixture.WriteClass("RunGoalServiceGraphAdmissionTests", isolated: false);
            var retries = new List<(TaskId TaskId, string Message, RetryCause Cause)>();
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
                    retries.Add((taskId, message, cause));
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordTaskNote: (_, _, message) => notes.Add(message),
                executionDirectory: executionDirectory);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var retry = Assert.Single(retries);
            Assert.Equal(developer.Id, retry.TaskId);
            Assert.Equal(RetryCause.NewSourceFinding, retry.Cause);
            Assert.Contains(FixturePath, retry.Message, StringComparison.Ordinal);
            Assert.Contains("RunGoalServiceGraphAdmissionTests", retry.Message, StringComparison.Ordinal);
            Assert.Contains("[lane-partition]", retry.Message, StringComparison.Ordinal);
            Assert.Contains("not assigned to any acceptance lane", retry.Message, StringComparison.Ordinal);
            Assert.Equal([AgentRole.Developer], dispatchedRoles);
            Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
            Assert.Null(tester.LastDispatch);
            Assert.StartsWith("developer-completion structural pre-check failed:", Assert.Single(notes), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    [Fact]
    public void Single_lane_class_keeps_pass_message_and_does_not_retry_developer()
    {
        var executionDirectory = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal("Single lane structural preflight");
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
                PassVerification(kernel, goal, task);
            using var fixture = new Fixture(GoalWorktrees.WorktreePath(executionDirectory, goal.Id));
            File.WriteAllText(Path.Combine(fixture.Root, "config/acceptance-manifest.json"), PartitionManifest);
            fixture.WriteClass("RunGoalServiceGraphAdmissionTests", isolated: false);
            var unassigned = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);
            Assert.True(unassigned.HasViolation, unassigned.Message);
            Assert.Contains("[lane-partition]", unassigned.Message, StringComparison.Ordinal);
            fixture.WriteClass("GoalCardRenderingTests", isolated: false);

            var result = DeveloperCompletionStructuralPreflight.Evaluate(fixture.Root);

            Assert.False(result.HasViolation, result.Message);
            Assert.Equal(PassMessage, result.Message);
            var retries = new List<TaskId>();
            var notes = new List<string>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => DispatchStartOutcome.Started(),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retries.Add(taskId);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordTaskNote: (_, _, message) => notes.Add(message),
                executionDirectory: executionDirectory);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Empty(retries);
            Assert.Contains(notes, note => note.StartsWith(
                "developer-completion structural pre-check passed:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(executionDirectory, recursive: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        internal string ExemptionsPath => Path.Combine(Root, TestCollectionGuardRules.ExemptionsRelativePath);

        internal Fixture(string? root = null)
        {
            Root = root ?? ConductorDriverTests.CreateTempDirectory();
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            Directory.CreateDirectory(Path.Combine(Root, TestDirectory));
            var repository = VerifiedRepositoryRoot.Find();
            foreach (var path in new[] { "config/acceptance-manifest.json", TestCollectionGuardRules.ExemptionsRelativePath,
                         TestDirectory + "/LaneIsolatedRootGuardTests.cs" })
                File.Copy(Path.Combine(repository, path), Path.Combine(Root, path));
            var authorityPath = Path.Combine(Root, SourceSizeRatchet.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(authorityPath)!);
            File.WriteAllText(authorityPath, "new SourceSizeCeiling(\"guarded.cs\", 3)");
            File.WriteAllLines(Path.Combine(Root, "guarded.cs"), ["line", "line", "line"]);
        }

        internal void WriteClass(string name, bool isolated, string? baseName = null)
        {
            var collection = isolated ? "[Xunit." + "Collection(\"DotnetBuildSlots\")]\n" : "";
            File.WriteAllText(Path.Combine(Root, FixturePath), collection + "public " + "class " + name +
                (baseName is null ? "" : " : " + baseName) + " { [Xunit.Fact] public void Example() { } }");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
