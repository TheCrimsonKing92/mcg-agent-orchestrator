using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed partial class ConductorDriverTestsFindingEvidence
{
    [Xunit.Fact]
    public void FindingEvidenceRequestAcceptsRegisteredCliInfrastructureProject()
    {
        const string cliProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/" +
            "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The extracted CLI project needs focused evidence.",
            id: "cli-extracted-project",
            project: "Infrastructure.Cli.Tests",
            classes: ["CliArgumentNormalizationTests", "CliCommandTestsAddTaskCommands"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "registered extracted project", findings: [finding]);
        string? request = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => new AcceptanceGateEngineSettings
            {
                MtpInvocations = [new AcceptanceMtpInvocation { Project = cliProject }]
            },
            runFocusedEvidence: (_, value) =>
            {
                request = value;
                return new FocusedEvidenceRunResult(value, true, true, "registered project passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            "Infrastructure.Cli.Tests:CliArgumentNormalizationTests; Infrastructure.Cli.Tests:CliCommandTestsAddTaskCommands",
            request);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        Assert.True(recorded.Single(item => item.StableId == "cli-extracted-project").EvidenceOutcome?.Honoured);
    }

    [Xunit.Theory]
    [Xunit.InlineData("../../../evil/tests/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj")]
    [Xunit.InlineData("tests/../../evil/tests/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj")]
    [Xunit.InlineData("C:/attacker/tests/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj")]
    [Xunit.InlineData("\\\\server\\share\\tests\\Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.RealProcessShardProbe")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/RealProcessShardProbe/Mcg.AgentOrchestrator.RealProcessShardProbe.csproj")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj")]
    [Xunit.InlineData("Undeclared.Tests")]
    public void FindingEvidenceRequestRefusesTraversalAbsoluteAndNonTestProjects(string unsupportedProject)
    {
        const string cliProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/" +
            "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
        const string probeProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/RealProcessShardProbe/" +
            "Mcg.AgentOrchestrator.RealProcessShardProbe.csproj";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Undeclared project requests must be refused.",
            id: "refused-request",
            project: unsupportedProject,
            classes: ["CliCommandTestsTaskQueries"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "negative control evidence request", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => new AcceptanceGateEngineSettings
            {
                MtpInvocations =
                [
                    new AcceptanceMtpInvocation { Project = cliProject },
                    new AcceptanceMtpInvocation { Project = probeProject }
                ]
            },
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "must not run", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "refused-request")
            .EvidenceOutcome;
        Assert.False(outcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, outcome?.Reason);
        Assert.Null(outcome?.ReceiptId);
        Assert.Contains("Accepted forms: Core, Core.Tests", outcome?.Detail, StringComparison.Ordinal);
        Assert.Contains("(declared: Infrastructure.Cli.Tests)", outcome?.Detail, StringComparison.Ordinal);
    }
}
