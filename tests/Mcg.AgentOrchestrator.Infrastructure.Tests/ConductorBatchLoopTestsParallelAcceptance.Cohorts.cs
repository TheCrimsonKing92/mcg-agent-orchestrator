using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class ConductorBatchLoopTestsParallelAcceptance
{
    [Xunit.Fact]
    public void ProductionCohortRunsOneSharedGateAndBypassesOrdinaryMemberGates()
    {
        var root = CreateTempDirectory("mcg-speculative-cohort-advisory");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "Update first advisory source");
        var second = CreateVerifiedSimpleGoal(kernel, "Update second advisory source");
        var statusesBefore = new[] { first.Status, second.Status };
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/FirstAdvisory.razor"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondAdvisoryTests.cs"]
        };
        var acceptanceCalls = new List<string>();
        var landed = new List<string>();
        var cohortCalls = 0;
        string? sharedReceiptId = null;
        var mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    acceptanceCalls.Add(goal.Id.Value);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                land: goal =>
                {
                    landed.Add(goal.Id.Value);
                    return new LandingResult(
                        goal.Id.Value,
                        goal.Id.Value[..8],
                        new LandingDecision.Promote(),
                        "integration",
                        true,
                        "ok");
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runAcceptanceCohort: (selection, goals, policy) =>
                {
                    cohortCalls++;
                    var identity = AcceptanceCohortIdentity.Create(
                        selection.BindMembers(),
                        mainRevision,
                        "dddddddddddddddddddddddddddddddddddddddd",
                        "manifest-v1");
                    var receipt = new AcceptanceCohortReceipt(
                        $"receipt-{identity.Value}",
                        identity,
                        AcceptanceCohortGateOutcome.Passed,
                        DateTimeOffset.UtcNow,
                        10,
                        [],
                        GateExitCode: 0,
                        GateTestResultPaths: [Path.GetFullPath("production-cohort.trx")],
                        ValidForLanding: true);
                    sharedReceiptId = receipt.ReceiptId;
                    return new ConductorAcceptanceCohortRunResult(
                        receipt,
                        goals.Where(goal => selection.Members.Any(member => member.GoalId == goal.Id)).ToDictionary(
                            goal => goal.Id.Value,
                            goal => new ConductorAdvanceResult(
                                goal.Id.Value,
                                goal.Id.Value[..8],
                                policy.Name,
                                new ConductorAdvanceOutcome.Executed(
                                    GoalLifecycleState.Verified,
                                    $"shared receipt {receipt.ReceiptId}")),
                            StringComparer.Ordinal),
                        $"outcome=passed receipt={receipt.ReceiptId}");
                });

            var summary = new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            var cohortEvents = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "speculative-cohort-plan")
                .ToArray();

            Assert.Equal(2, summary.Advanced);
            Assert.Equal(1, cohortCalls);
            Assert.Empty(acceptanceCalls);
            Assert.Empty(landed);
            Assert.NotNull(sharedReceiptId);
            Assert.Equal(statusesBefore, new[] { first.Status, second.Status });
            var receipt = Assert.Single(cohortEvents);
            Assert.Null(receipt.GoalId);
            Assert.Contains("advisory=true", receipt.Detail, StringComparison.Ordinal);
            Assert.Contains(first.Id.Value[..8], receipt.Detail, StringComparison.Ordinal);
            Assert.Contains(second.Id.Value[..8], receipt.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void CohortExit_InfrastructureFailureEmitsReason()
    {
        var root = CreateTempDirectory("mcg-cohort-infrastructure-reason");
        var logPath = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var kernel = new AgentOrchestratorKernel();
        var first = CreateVerifiedSimpleGoal(kernel, "First infrastructure failure member");
        var second = CreateVerifiedSimpleGoal(kernel, "Second infrastructure failure member");
        var statusesBefore = new[] { first.Status, second.Status };
        var paths = new Dictionary<GoalId, IReadOnlyList<string>>
        {
            [first.Id] = ["src/Mcg.AgentOrchestrator.App/Dashboard/Components/FirstInfrastructureFailure.razor"],
            [second.Id] = ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondInfrastructureFailureTests.cs"]
        };
        var mainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var projector = new GateReadyCandidateProjector(
            goalId => new GateReadyCandidateRevisionPair(
                goalId.Value.PadRight(40, 'b')[..40],
                mainRevision),
            goalId => new GateReadyLandingScopeObservation(true, paths[goalId]),
            (_, _, _) => new GateReadyMergeTreeObservation(true));

        try
        {
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                land: goal => new LandingResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "ok"),
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                runAcceptanceCohort: (selection, goals, policy) =>
                {
                    var identity = AcceptanceCohortIdentity.Create(
                        selection.BindMembers(),
                        mainRevision,
                        "dddddddddddddddddddddddddddddddddddddddd",
                        "manifest-v1");
                    var receipt = new AcceptanceCohortReceipt(
                        $"receipt-{identity.Value}",
                        identity,
                        AcceptanceCohortGateOutcome.InfrastructureFailure,
                        DateTimeOffset.UtcNow,
                        10,
                        ["infrastructure tests"],
                        GateExitCode: 2,
                        GateTestResultPaths: [],
                        ValidForLanding: false,
                        InfrastructureReasonCode: AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                        InfrastructureDetail: "Acceptance verification did not produce coherent TRX evidence.");
                    return new ConductorAcceptanceCohortRunResult(
                        receipt,
                        goals.Where(goal => selection.Members.Any(member => member.GoalId == goal.Id)).ToDictionary(
                            goal => goal.Id.Value,
                            goal => new ConductorAdvanceResult(
                                goal.Id.Value,
                                goal.Id.Value[..8],
                                policy.Name,
                                new ConductorAdvanceOutcome.Held(
                                    GoalLifecycleState.Verified,
                                    "shared infrastructure failure receipt")),
                            StringComparer.Ordinal),
                        $"outcome={receipt.Outcome} receipt={receipt.ReceiptId}");
                });

            _ = new ConductorBatchLoop(
                conductEventLogWriter: new ConductEventLogWriter(logPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);
            var exit = File.ReadAllLines(logPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Single(record => record.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT", StringComparison.Ordinal));

            var exitTokens = exit.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains("outcome=InfrastructureFailure", exitTokens);
            Assert.Contains("reason=trx-evidence-incoherent", exitTokens);
            Assert.DoesNotContain("reason=trx-evidence-incoherent-extra", exitTokens);
            Assert.Equal(statusesBefore, new[] { first.Status, second.Status });
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }
}
