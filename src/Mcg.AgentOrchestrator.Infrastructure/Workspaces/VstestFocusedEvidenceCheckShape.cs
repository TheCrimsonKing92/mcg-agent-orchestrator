using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Narrows a check declaration while retaining its VSTest invocation context.</summary>
internal static class VstestFocusedEvidenceCheckShape
{
    internal static bool TryResolve(
        string project,
        AcceptanceGateEngineSettings settings,
        out AcceptanceManifestCheck check)
    {
        check = null!;
        return !settings.HasMtpInvocation(project) &&
            CheckDeclaredTestProjects.TryFindDeclaringCheck(project, settings.DeclaredDotnetTestChecks, out check);
    }

    internal static IReadOnlyList<string> Arguments(AcceptanceManifestCheck check, string? focusedFilter)
    {
        if (string.IsNullOrWhiteSpace(focusedFilter))
        {
            return check.Arguments;
        }

        var arguments = new List<string>();
        string? declaredFilter = null;
        for (var index = 0; index < check.Arguments.Count; index++)
        {
            var argument = check.Arguments[index];
            if (argument.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= check.Arguments.Count)
                {
                    throw new InvalidDataException($"Acceptance check '{check.Name}' has a --filter without a value.");
                }

                declaredFilter = check.Arguments[++index];
            }
            else if (argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            {
                declaredFilter = argument["--filter=".Length..];
            }
            else
            {
                arguments.Add(argument);
            }
        }

        arguments.Add("--filter");
        arguments.Add(string.IsNullOrWhiteSpace(declaredFilter)
            ? focusedFilter
            : $"({declaredFilter})&({focusedFilter})");
        return arguments;
    }
}
