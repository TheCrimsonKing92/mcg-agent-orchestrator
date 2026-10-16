using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Console output uses the repository's serialized console collection; fixtures have unique temp roots.
[Collection(CliTestCollections.ConsoleSerialized)]
public sealed class CliCommandTestsFindingLookup : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finding_PrintsRecordAndReceiptsWithoutChangingSnapshot(bool scoped)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = CliFindingTestFixture.CreateSeed();
            var goal = kernel.Goals.First();
            var before = JsonSerializer.Serialize(kernel.ExportSnapshot());
            var args = scoped ? new[] { "finding", "--goal", goal.Id.Value, CliFindingTestFixture.Id }
                : ["finding", CliFindingTestFixture.Id];
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var changed = true;
            var output = CaptureConsole(() => changed = CliCommandDispatcher.ExecuteCommand(args, kernel,
                OrchestratorWorkspace.ForDirectory(root), ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal));
            Assert.False(changed);
            Assert.Null(currentGoal);
            Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
            AssertFindingOutput(output, goal);

            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            var storedBefore = JsonSerializer.Serialize(repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult());
            output = CliFindingTestFixture.Execute(args, repository, OrchestratorWorkspace.ForDirectory(root));
            AssertFindingOutput(output, goal);
            Assert.Equal(storedBefore, JsonSerializer.Serialize(repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult()));
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingReceiptAndNoRequest_PrintDistinctReasons()
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(CliFindingTestFixture.CreateSeed());
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Assert.Contains(FindingLookup.MissingReceiptReason("missing-receipt"),
                CliFindingTestFixture.Execute(["finding", "missing-receipt"], repository, workspace));
            Assert.Contains("no evidence requested",
                CliFindingTestFixture.Execute(["finding", "no-request"], repository, workspace));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void QueryErrors_PreserveUsageAndGoalResolutionContracts()
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(CliFindingTestFixture.CreateSeed()) { ThrowOnOutbox = true };
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Assert.Contains("No finding matches absent", Assert.Throws<KeyNotFoundException>(() =>
                CliFindingTestFixture.Execute(["finding", "absent"], repository, workspace)).Message);
            foreach (var args in new string[][] { ["finding"], ["finding", " "], ["finding", "--goal", "abc1"] })
                Assert.Contains("Usage:", Assert.Throws<ArgumentException>(() =>
                    CliFindingTestFixture.Execute(args, repository, workspace)).Message);
            Assert.Equal("Goal 'absent' was not found.", Assert.Throws<KeyNotFoundException>(() =>
                CliFindingTestFixture.Execute(["finding", "--goal", "absent", "build"], repository, workspace)).Message);
            Assert.Equal("Goal prefix 'abc' is ambiguous.", Assert.Throws<InvalidOperationException>(() =>
                CliFindingTestFixture.Execute(["finding", "--goal", "abc", "build"], repository, workspace)).Message);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MergeSaveAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HelpAndInvalidFlags_DoNotReadState()
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(CliFindingTestFixture.CreateSeed()) { ThrowOnOutbox = true };
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var output = CliFindingTestFixture.Execute(["finding", "--help"], repository, workspace);
            Assert.Contains($"Usage: {CliFindingQueryCommand.Usage}", output);
            Assert.Throws<ArgumentException>(() => CliFindingTestFixture.Execute(
                ["finding", "--goal", "absent", "id", "--bogus"], repository, workspace));
            Assert.Equal(0, repository.ListGoalMetadataCount);
            Assert.Equal(0, repository.LoadGoalsCount);
            Assert.Equal(0, repository.MutationAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Runbook_ListsFindingUsageLine()
    {
        var runbook = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operator-runbook.md"));
        Assert.Contains($"`{CliFindingQueryCommand.Usage}`", runbook);
        Assert.Contains("replaces searching `.orchestrator` for a finding id", runbook);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") => VerifiedRepositoryRoot.Find(sourcePath);

    private static void AssertFindingOutput(string output, Goal goal)
    {
        foreach (var expected in new[] { CliFindingTestFixture.Id, "Blocking", "Open", "TestEvidence",
            "build.cs::Build", "Build timed out before evidence replay", goal.Id.Value,
            goal.Tasks[0].Id.Value, "task 1", "Reviewer", "candidate-sha", "2026-10-16T07:00:00",
            "Receipt receipt-green: candidate candidate-sha, Accepted=True, Passed=True, Summary=green summary",
            "Receipt receipt-red: candidate candidate-sha, Accepted=True, Passed=False, Summary=red summary",
            "Arm Candidate: sha candidate-sha, Green", "Arm Baseline: sha baseline-sha, Red" })
            Assert.Contains(expected, output);
    }
}

internal static class CliFindingTestFixture
{
    internal const string Id = "replay-evidence-build-timeout";

    internal static AgentOrchestratorKernel CreateSeed()
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Finding goal",
            [new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), "Other goal",
            [new TaskSpec(TaskId.New(), "Review other", AgentRole.Reviewer)]);
        var snapshot = kernel.ExportSnapshot();
        var goal = snapshot.Goals.First();
        var request = new FindingEvidenceRequest([new("tests/Core.csproj", "BuildTests")]);
        var finding = new ReviewFinding(Id, ReviewFindingState.Open, new("build.cs", "Build"),
            "Build timed out before evidence replay", Category: FindingCategory.TestEvidence, EvidenceRequest: request);
        var record = new TaskVerificationSnapshot("review", "", 0, "", "",
            DateTimeOffset.Parse("2026-10-16T07:00:00Z"), ReviewedCommit: "candidate-sha",
            MergedReviewFindings: [finding, finding with { StableId = "missing-receipt", EvidenceRequest = new([]) },
                finding with { StableId = "no-request", EvidenceRequest = null }],
            FindingEvidenceReceipts:
            [new("receipt-green", "candidate-sha", request, true, true, "green summary",
                [new(FindingEvidenceArm.Candidate, "candidate-sha", FindingEvidenceArmDisposition.Green, true, true, "passed")]),
             new("receipt-red", "candidate-sha", request, true, false, "red summary",
                [new(FindingEvidenceArm.Baseline, "baseline-sha", FindingEvidenceArmDisposition.Red, true, false, "failed")])]);
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goal with { Tasks = [goal.Tasks.Single() with { VerificationHistory = [record] }] }, snapshot.Goals.Last()]
        });
    }

    internal static string Execute(IReadOnlyList<string> args, ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var changed = true;
        var output = AsyncLocalConsoleRouter.Capture(() => changed = CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
        Assert.False(changed);
        Assert.Null(currentGoal);
        return output;
    }
}
