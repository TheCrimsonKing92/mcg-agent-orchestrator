using System.Security.Cryptography;
using System.Text;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceIdenticalTreeReuseRule
{
    internal static bool TryGetCheckIdentity(AcceptanceManifestCheck check, out string identity)
    {
        identity = string.Empty;
        if (!check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(check.Project))
            return false;

        var project = check.Project.Replace('\\', '/').ToLowerInvariant();
        if (project is not ("tests/mcg.agentorchestrator.core.tests/mcg.agentorchestrator.core.tests.csproj" or
            "tests/mcg.agentorchestrator.infrastructure.tests/cli/mcg.agentorchestrator.infrastructure.cli.tests.csproj" or
            "tests/mcg.agentorchestrator.infrastructure.tests/acceptance/mcg.agentorchestrator.infrastructure.acceptance.tests.csproj"))
            return false;

        var input = project + "\n" + string.Join('\n', check.Arguments);
        identity = "check-identity-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();
        return true;
    }

    internal static string PartitionId(AcceptanceManifestCheck check) =>
        check.Project!.Replace('\\', '/').ToLowerInvariant() switch
        {
            "tests/mcg.agentorchestrator.core.tests/mcg.agentorchestrator.core.tests.csproj" => "core-tests",
            "tests/mcg.agentorchestrator.infrastructure.tests/cli/mcg.agentorchestrator.infrastructure.cli.tests.csproj" => "cli-tests",
            "tests/mcg.agentorchestrator.infrastructure.tests/acceptance/mcg.agentorchestrator.infrastructure.acceptance.tests.csproj" => "acceptance-execution-owner-tests",
            _ => throw new InvalidOperationException("An identical-tree partition requires an eligible project.")
        };

    internal static string? UncacheableReasonCode(string checkName) => checkName switch
    {
        "provider environment tests" => "host-state-dependent",
        "real process shard probe tests" => "gate-apparatus-probe",
        "git diff whitespace" => "worktree-diff-dependent",
        _ => null
    };
}
