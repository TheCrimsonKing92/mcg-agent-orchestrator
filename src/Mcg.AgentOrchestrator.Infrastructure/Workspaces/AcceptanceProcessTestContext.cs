using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns focused-test inputs while tests continue through the production process invoker.
internal static class AcceptanceProcessTestContext
{
    internal static GateHeartbeatContext Heartbeat(string[] arguments, string heartbeatPath) =>
        new("test-goal", "verification-check", "owned-child-cleanup", null, heartbeatPath, null,
            string.Join(' ', arguments.Select(ProcessInvokerAccess.QuoteForDisplay)), null);

    internal static Func<Process, SpawnProcessIdentityReadResult>? IdentityReader(
        Func<Process, SpawnProcessIdentity>? reader) =>
        reader is null ? null : process => new SpawnProcessIdentityReadResult(reader(process), 1, "test-identity-seam");

    internal static Task<CommandResult> RunManifestCommandAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        IReadOnlyDictionary<string, string?> childEnvironmentOverrides,
        bool resolveBareCommand,
        CancellationToken cancellationToken) =>
        AcceptanceCheckProcessInvoker.RunAsync(
            arguments, workingDirectory, commandTimeout, false, cancellationToken, default, null,
            new AcceptanceProcessRequest
            {
                ResolveBareCommand = resolveBareCommand,
                ChildEnvironmentOverrides = childEnvironmentOverrides,
            });
}
