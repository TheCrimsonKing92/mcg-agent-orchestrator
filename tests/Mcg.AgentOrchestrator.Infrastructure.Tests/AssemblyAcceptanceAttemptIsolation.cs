using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Tests in this assembly construct GoalAcceptanceVerifier instances with fake command
// runners whose fixtures write TRX receipts to whatever path the verifier resolves. The
// verifier resolves its receipt directory and attempt-id prefix from the
// MCG_ACCEPTANCE_GATE_ATTEMPT_TRX_PREFIX environment variable. The build manager likewise
// resolves attempt custody from MCG_ACCEPTANCE_GATE_ATTEMPT_ID and its liveness hint. When
// this assembly itself executes inside a real acceptance gate attempt, the child process
// inherits that OUTER attempt state. Fixture-hosted verifiers can then replace genuine
// receipts, while isolated build-manager tests can create live outer-attempt custody
// markers in their private slots.
//
// Clear inherited attempt state once at module load, before any test runs. Tests that
// exercise these variables set them themselves after load and are unaffected; the gate
// passes receipt paths to test children as explicit argv, so no child needs the inherited
// values.
internal static class AssemblyAcceptanceAttemptIsolation
{
    [ModuleInitializer]
    internal static void Install()
    {
        Environment.SetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
            null,
            EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(
            AcceptanceAttemptArtifactCustody.AttemptIdVariable,
            null,
            EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable(
            AcceptanceAttemptArtifactCustody.LivenessCheckHintVariable,
            null,
            EnvironmentVariableTarget.Process);
    }
}
