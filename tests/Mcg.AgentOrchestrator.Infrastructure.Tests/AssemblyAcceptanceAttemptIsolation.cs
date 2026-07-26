using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Tests in this assembly construct GoalAcceptanceVerifier instances with fake command
// runners whose fixtures write TRX receipts to whatever path the verifier resolves. The
// verifier resolves its receipt directory and attempt-id prefix from the
// MCG_ACCEPTANCE_GATE_ATTEMPT_TRX_PREFIX environment variable. When this assembly itself
// executes inside a real acceptance gate attempt, the child process inherits the OUTER
// attempt's prefix, so every fixture-hosted verifier resolves the real attempt's receipt
// paths: it deletes the gate's genuine TRX receipts as stale and replaces them with
// fixture content (observed 2026-07-26, gate attempt 372fd506: 22 receipts reduced to
// empty stubs, structural coverage correctly refused the attempt).
//
// Clear the inherited variable once at module load, before any test runs. Tests that
// exercise the prefix behavior set the variable themselves after load and are unaffected;
// the gate passes receipt paths to test children as explicit argv, so no child needs the
// inherited value.
internal static class AssemblyAcceptanceAttemptIsolation
{
    [ModuleInitializer]
    internal static void Install() =>
        Environment.SetEnvironmentVariable(
            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable,
            null,
            EnvironmentVariableTarget.Process);
}
