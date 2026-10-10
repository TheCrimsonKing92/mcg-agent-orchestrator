using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Single authoritative source of the test projects that focused evidence may target.
/// Entries derive from <c>engine.mtpInvocations</c> and explicitly supplied <c>dotnet-test</c> checks;
/// this type deliberately declares no project constants of its own so no parallel census can drift.
/// </summary>
internal static class DeclaredTestProjectInventory
{
    private const string DeclaredProjectRoot = "tests/";
    private const string DeclaredProjectSuffix = ".Tests.csproj";

    internal const string LegacyAliasProjectForms =
        "Core, Core.Tests, Mcg.AgentOrchestrator.Core.Tests, Infrastructure, Infrastructure.Tests, " +
        "Mcg.AgentOrchestrator.Infrastructure.Tests, or a full .csproj path ending in " +
        "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj or " +
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    /// <summary>
    /// The eligibility shape rule for a declared project: repository-relative, rooted under
    /// <c>tests/</c>, a <c>*.Tests.csproj</c> assembly, and free of traversal or absolute segments.
    /// A manifest entry that fails this rule is skipped rather than honoured.
    /// </summary>
    internal static bool IsDeclaredFocusedEvidenceProject(string? project)
    {
        var normalized = NormalizePath(project);
        if (string.IsNullOrWhiteSpace(normalized) ||
            !normalized.StartsWith(DeclaredProjectRoot, StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(DeclaredProjectSuffix, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(':') ||
            Path.IsPathRooted(normalized))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(segment) ||
                segment.Equals(".", StringComparison.Ordinal) ||
                segment.Equals("..", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The declared, eligible test projects in manifest order, each as its candidate-relative path.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredProjects(
        AcceptanceGateEngineSettings? engineSettings,
        IReadOnlyList<AcceptanceManifestCheck>? checks = null)
    {
        var declared = new List<string>();
        foreach (var invocation in engineSettings?.MtpInvocations ?? [])
        {
            var normalized = NormalizePath(invocation?.Project);
            if (!IsDeclaredFocusedEvidenceProject(normalized) ||
                declared.Contains(normalized!, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            declared.Add(normalized!);
        }

        foreach (var project in CheckDeclaredTestProjects.Projects(checks))
        {
            if (!declared.Contains(project, StringComparer.OrdinalIgnoreCase))
            {
                declared.Add(project);
            }
        }

        return declared;
    }

    /// <summary>
    /// Resolves a requested alias against the declared inventory. The resolved value is always the
    /// manifest-declared candidate-relative path, never the caller's alias, so an absolute or
    /// traversal-bearing request can never widen where a focused run executes.
    /// </summary>
    internal static bool TryResolve(
        string? alias,
        AcceptanceGateEngineSettings? engineSettings,
        out string project,
        IReadOnlyList<AcceptanceManifestCheck>? checks = null)
    {
        project = string.Empty;
        var normalizedAlias = NormalizePath(alias);
        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            return false;
        }

        foreach (var declared in DeclaredProjects(engineSettings, checks))
        {
            var fileName = Path.GetFileName(declared);
            if (normalizedAlias.Equals(declared, StringComparison.OrdinalIgnoreCase) ||
                normalizedAlias.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                normalizedAlias.Equals(
                    Path.GetFileNameWithoutExtension(fileName),
                    StringComparison.OrdinalIgnoreCase) ||
                normalizedAlias.Equals(ProjectLabel(declared), StringComparison.OrdinalIgnoreCase))
            {
                project = declared;
                return true;
            }
        }

        // Check names are additional bare aliases; path-bearing requests must match a declared path.
        if (!normalizedAlias.Contains('/') && !normalizedAlias.Contains(':') &&
            normalizedAlias is not "." and not "..")
        {
            var declaringCheck = (checks ?? []).FirstOrDefault(check =>
                CheckDeclaredTestProjects.IsEligible(check) &&
                normalizedAlias.Equals(check.Name, StringComparison.OrdinalIgnoreCase));
            if (declaringCheck is not null)
            {
                project = DeclaredProjects(engineSettings, checks).First(declared =>
                    declared.Equals(NormalizePath(declaringCheck.Project), StringComparison.OrdinalIgnoreCase));
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The worker-visible accepted-project text: the legacy aliases the resolver still honours,
    /// followed by the labels actually declared in this candidate's manifest.
    /// </summary>
    internal static string DescribeSupportedProjectForms(
        AcceptanceGateEngineSettings? engineSettings,
        IReadOnlyList<AcceptanceManifestCheck>? checks = null)
    {
        var declaredLabels = DeclaredProjects(engineSettings)
            .Select(ProjectLabel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();
        var described = declaredLabels.Length == 0
            ? LegacyAliasProjectForms
            : LegacyAliasProjectForms +
                "; test projects declared in engine.mtpInvocations also accept their project label, " +
                "file name, or full .csproj path (declared: " +
                string.Join(", ", declaredLabels) +
                ")";
        var checkLabels = CheckDeclaredTestProjects.Projects(checks)
            .Except(DeclaredProjects(engineSettings), StringComparer.OrdinalIgnoreCase)
            .Select(CheckDeclaredTestProjects.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();
        return checkLabels.Length == 0
            ? described
            : described + "; test projects declared as dotnet-test checks also accept their project label, " +
                "file name, or full .csproj path (declared: " + string.Join(", ", checkLabels) + ")";
    }
}
