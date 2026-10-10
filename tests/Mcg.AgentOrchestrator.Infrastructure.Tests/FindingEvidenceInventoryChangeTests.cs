using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using static CheckDeclaredFocusedEvidenceProjectTests;

// Parallel-safe: each case owns its temporary directories and in-memory kernel; no processes.
public sealed class FindingEvidenceInventoryChangeTests
{
    [Fact]
    public void ProjectStateManifest_NewProject_RunsIdenticalRequest() =>
        RunInventoryRounds(projectStateLayout: true, changeInventory: true);

    [Fact]
    public void ProjectStateManifest_UnchangedInventory_RetainsRefusal() =>
        RunInventoryRounds(projectStateLayout: true, changeInventory: false);

    [Fact]
    public void WorktreeManifest_NewProject_RunsIdenticalRequest() =>
        RunInventoryRounds(projectStateLayout: false, changeInventory: true);

    private static void RunInventoryRounds(bool projectStateLayout, bool changeInventory)
    {
        using var fixture = new Fixture(projectStateLayout);
        var settings = fixture.LoadSettings();
        Assert.Equal(CoreProject, Assert.Single(settings.DeclaredDotnetTestChecks).Project);
        var (kernel, goal, reviewer) = ReadyGoal();
        var finding = RequestedFinding();
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var runs = 0;
        string? capturedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => settings,
            runFocusedEvidence: (evidenceGoal, request) =>
            {
                runs++;
                capturedRequest = request;
                Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
                    request, settings, fixture.Worktree, out var checks, out _, out var rejection), rejection.Detail);
                var check = Assert.Single(checks);
                Assert.Equal(AppProject, check.Project);
                Assert.Equal("vstest", check.Runner);
                Assert.Equal(17, check.TimeoutMinutes);
                Assert.Equal(["--configuration", "Release", "--filter",
                    "(Category!=Slow)&(FullyQualifiedName~HealthProbeTests)"], check.Arguments);
                return new FocusedEvidenceRunResult(request, true, true, "focused evidence passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, runs);
        var first = LatestOutcome(reviewer);
        Assert.False(first.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, first.Reason);
        Assert.Null(first.ReceiptId);
        Assert.Equal(ExpectedIdentity(settings), first.DecisionReason);

        if (changeInventory) fixture.DeclareBothProjects();
        settings = fixture.LoadSettings();
        if (changeInventory) Assert.NotEqual(first.DecisionReason, ExpectedIdentity(settings));
        else Assert.Equal(first.DecisionReason, ExpectedIdentity(settings));
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        Assert.Equal(first, LatestOutcome(reviewer)); // The identical request carries the first refusal.

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var second = LatestOutcome(reviewer);
        if (changeInventory)
        {
            Assert.Equal(1, runs);
            Assert.Equal(AppProject + ":HealthProbeTests", capturedRequest);
            Assert.True(second.Honoured);
            Assert.False(string.IsNullOrWhiteSpace(second.ReceiptId));
            Assert.Equal(second.ReceiptId, Assert.Single(reviewer.VerificationHistory
                .SelectMany(item => item.FindingEvidenceReceipts ?? [])).ReceiptId);
        }
        else
        {
            Assert.Equal(0, runs);
            Assert.Null(capturedRequest);
            Assert.False(second.Honoured);
            Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, second.Reason);
            Assert.Null(second.ReceiptId);
            Assert.Equal(first.DecisionReason, second.DecisionReason);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("reused-red")]
    public void LegacyRefusal_DeclaredProject_RemainsPermanent(string? decisionReason)
    {
        using var fixture = new Fixture(projectStateLayout: true);
        fixture.DeclareBothProjects();
        var settings = fixture.LoadSettings();
        Assert.Contains(settings.DeclaredDotnetTestChecks, check => check.Project == AppProject);
        var (kernel, goal, reviewer) = ReadyGoal();
        var finding = RequestedFinding();
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var prior = new FindingEvidenceOutcome(Honoured: false,
            Reason: FindingEvidenceNotHonouredReason.UnsupportedProject, DecisionReason: decisionReason);
        kernel.RecordFindingEvidenceOutcome(goal.Id, reviewer.Id, finding.StableId, prior);
        kernel.RetryTask(goal.Id, reviewer.Id, "re-file the identical request with its legacy refusal");
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        Assert.Equal(prior, LatestOutcome(reviewer));
        var runs = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => settings,
            runFocusedEvidence: (_, request) =>
            {
                runs++;
                return new FocusedEvidenceRunResult(request, true, true, "focused evidence passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, runs);
        Assert.Equal(prior, LatestOutcome(reviewer));
        Assert.Empty(reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []));
    }

    [Fact]
    public void ExecutorRefusal_UnsupportedProject_RecordsInventoryIdentity()
    {
        using var fixture = new Fixture(projectStateLayout: true);
        fixture.DeclareBothProjects();
        var settings = fixture.LoadSettings();
        var (kernel, goal, reviewer) = ReadyGoal();
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [RequestedFinding()]);
        var runs = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => settings,
            runFocusedEvidence: (_, request) =>
            {
                runs++;
                return new FocusedEvidenceRunResult(request, false, false, "project is unsupported", [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnsupportedProject, AppProject, "project is unsupported"));
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var first = LatestOutcome(reviewer);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, first.Reason);
        Assert.Equal(ExpectedIdentity(settings), first.DecisionReason);
        Assert.Null(first.ReceiptId);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [RequestedFinding()]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, runs);
        Assert.Equal(first, LatestOutcome(reviewer));
    }

    private static string ExpectedIdentity(AcceptanceGateEngineSettings settings) =>
        "focused-inventory-v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            GoalAcceptanceVerifier.FocusedEvidenceSupportedProjectForms(settings))))[..16];

    private static ReviewFinding RequestedFinding() => EvidenceFindingWithRequest(
        "Foreign app project requires focused evidence.", id: "inventory-change",
        project: "NetHealth.App.Tests", classes: ["HealthProbeTests"]);

    private static FindingEvidenceOutcome LatestOutcome(TaskSpec reviewer) => Assert.IsType<FindingEvidenceOutcome>(
        Assert.Single(reviewer.VerificationHistory.Last().MergedReviewFindings!).EvidenceOutcome);

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer) ReadyGoal()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        return (kernel, goal, reviewer);
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string Worktree { get; }
        private string? ProjectHome { get; }
        private string ManifestPath { get; }

        internal Fixture(bool projectStateLayout)
        {
            Worktree = Path.Combine(Root, "worktree");
            ProjectHome = projectStateLayout ? Path.Combine(Root, "state") : null;
            ManifestPath = Path.Combine(ProjectHome ?? Path.Combine(Worktree, "config"), "acceptance-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            foreach (var project in new[] { CoreProject, AppProject })
            {
                var projectPath = Path.Combine(Worktree, project);
                Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
                File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "HealthProbeTests.cs"),
                    "public class HealthProbeTests { }");
            }
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
            {
                checks = new[] { new { name = "core tests", type = "dotnet-test", runner = "vstest", project = CoreProject } }
            }));
            Assert.Equal(!projectStateLayout, File.Exists(Path.Combine(Worktree, "config", "acceptance-manifest.json")));
        }

        internal AcceptanceGateEngineSettings LoadSettings() => AcceptanceGateEngineSettings.Load(Worktree, ProjectHome);
        internal void DeclareBothProjects() => File.WriteAllText(ManifestPath, Manifest);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
