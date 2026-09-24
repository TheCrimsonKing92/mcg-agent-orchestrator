using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorStructuralCoveragePermitOutcomeTests
{
    [Theory]
    [InlineData("structural-coverage-permit-unavailable", "structural-coverage-permit-unavailable",
        "StructuralCoveragePermitUnavailable")]
    [InlineData("trusted-main-build-failed", "infrastructure-deferred", "InfrastructureDeferred")]
    public void PermitTimeoutKeepsDistinctFaultAndAttemptOutcome(
        string reasonCode,
        string expectedFaultKind,
        string expectedOutcome)
    {
        var root = Path.Combine(Path.GetTempPath(), $"structural-permit-outcome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Classify structural coverage permit timeout");
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal, 0, [], "branch-permit", "main-permit");
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, acquireStableSlotLease: (_, _) => null);
            var attempt = coordinator.CreateAttemptForTests(candidate);
            coordinator.RunAttemptForTests(
                attempt,
                candidate,
                ConductorAutonomyPolicy.Permissive,
                (runCandidate, _, _, _, _) => ConductorParallelAcceptanceRunResult.Fault(
                    runCandidate,
                    new AcceptanceInfrastructureDeferredException(
                        reasonCode, null, "permit=build-0 holder=pid-123")));

            using var result = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
            Assert.Equal(expectedFaultKind, result.RootElement.GetProperty("faultKind").GetString());
            Assert.Equal(reasonCode, result.RootElement.GetProperty("infrastructureReasonCode").GetString());
            var persisted = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(attempt.MetadataPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(Enum.Parse<ConductorParallelAcceptanceAttemptOutcome>(expectedOutcome), persisted?.Outcome);
            Assert.NotEqual(ConductorParallelAcceptanceAttemptOutcome.Passed, persisted?.Outcome);
            Assert.True(ConductorParallelAcceptanceAttemptCoordinator.IsBoundedInfrastructureOutcome(
                persisted!.Outcome));
            Assert.Equal(expectedFaultKind, ConductorBatchLoop.AcceptanceAttemptOutcomeToken(persisted.Outcome));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
