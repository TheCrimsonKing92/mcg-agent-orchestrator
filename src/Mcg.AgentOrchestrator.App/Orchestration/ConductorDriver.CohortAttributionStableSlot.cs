using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal const int CohortAttributionStableSlotRoundCount = 20;
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
            : _parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLeaseInRounds(identity.Value,
                $"cohort-partition:goal-{member.GoalId.Value[..8]}", CohortAttributionStableSlotRoundCount, cancellationToken)
                ?? throw new CohortStableSlotsDeferredException(identity.Value);
        return AcceptanceExecutionRunner.RunAttempt(
            verifier, partitionPath, member.GoalId, member.LandingPaths,
            lease.Environment.BuildPermitIndex, lease, cancellationToken,
            CreateCohortAttributionExecutionOptions(gateProgressEventWriter, identity, member));
    }
}
