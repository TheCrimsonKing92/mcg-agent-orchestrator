using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class JunitCommandCheckDefinition
{
    internal static void Validate(Check check)
    {
        if (string.IsNullOrWhiteSpace(check.Command))
            throw new ArgumentException($"JUnit command check '{check.Name}' requires a nonempty command.");
        if (!IsRelativePath(check.Project ?? "."))
            throw new ArgumentException($"JUnit command check '{check.Name}' project must be relative without '..' segments.");
        if (check.Arguments is null || check.Arguments.Any(argument => argument is null))
            throw new ArgumentException($"JUnit command check '{check.Name}' arguments must be an array of strings.");
        if (check.Reports is not { Count: > 0 } || check.Reports.Any(report => !IsRelativePath(report)))
            throw new ArgumentException($"JUnit command check '{check.Name}' reports require relative globs without '..' segments.");
    }

    internal static bool IsRelativePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !path.StartsWith('/') && !path.StartsWith('\\') && !path.Contains(':') && !Path.IsPathRooted(path) &&
        !path.Split(['/', '\\']).Contains("..", StringComparer.Ordinal);
}
