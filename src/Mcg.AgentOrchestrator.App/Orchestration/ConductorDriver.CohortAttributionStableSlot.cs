using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Func<string, CancellationToken, DotnetBuildEnvironmentLease>? CohortPartitionStableSlotLeaseSource { get; set; }

    private AcceptanceVerificationResult RunCohortPartitionAttempt(
        IGoalAcceptanceVerifier verifier,
        string partitionPath,
        AcceptanceCohortMemberBinding member,
        AcceptanceCohortIdentity identity,
        ConductEventLogWriter gateProgressEventWriter,
        CancellationToken cancellationToken)
    {
        using var lease = CohortPartitionStableSlotLeaseSource is { } source
            ? source(identity.Value, cancellationToken)
            : _parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease(identity.Value, cancellationToken);
        return AcceptanceExecutionRunner.RunAttempt(
            verifier, partitionPath, member.GoalId, member.LandingPaths,
            lease.Environment.BuildPermitIndex, lease, cancellationToken,
            CreateCohortAttributionExecutionOptions(gateProgressEventWriter, identity, member));
    }
}
