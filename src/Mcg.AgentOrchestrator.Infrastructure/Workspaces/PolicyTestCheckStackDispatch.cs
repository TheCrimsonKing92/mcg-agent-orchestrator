using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class PolicyTestCheckStackDispatch
{
    // This is the one place a policy check's stack is chosen.
    internal static AcceptanceManifestCheck? ToManifestCheck(
        string worktreePath,
        VerificationPolicyCheck check)
    {
        if (check.Kind.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
        {
            return DotnetTestCheckTranslator.TryFromCommandLine(worktreePath, check.Name, check.CommandLine);
        }

        return null;
    }
}
