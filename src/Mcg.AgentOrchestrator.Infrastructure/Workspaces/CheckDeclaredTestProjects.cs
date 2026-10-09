using System.Text.Json;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Derives focused-evidence declarations from manifest-authored dotnet-test checks.</summary>
internal static class CheckDeclaredTestProjects
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static IReadOnlyList<AcceptanceManifestCheck> Read(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("checks", out var checks) ||
            checks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var declared = new List<AcceptanceManifestCheck>();
        foreach (var element in checks.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            try
            {
                var check = element.Deserialize<AcceptanceManifestCheck>(JsonOptions);
                if (IsEligible(check))
                {
                    declared.Add(check!);
                }
            }
            catch (JsonException)
            {
                // An unreadable check contributes no focused-evidence declaration.
            }
        }

        return declared;
    }

    internal static IEnumerable<string> Projects(IReadOnlyList<AcceptanceManifestCheck>? checks) =>
        (checks ?? [])
            .Where(IsEligible)
            .Select(check => NormalizePath(check.Project)!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    internal static bool TryFindDeclaringCheck(
        string project,
        IReadOnlyList<AcceptanceManifestCheck>? checks,
        out AcceptanceManifestCheck check)
    {
        check = (checks ?? []).FirstOrDefault(candidate =>
            IsEligible(candidate) &&
            string.Equals(NormalizePath(candidate.Project), NormalizePath(project),
                StringComparison.OrdinalIgnoreCase))!;
        return check is not null;
    }

    internal static string Label(string project)
    {
        var label = ProjectLabel(project);
        return label.Equals(project, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(project)
            : label;
    }

    internal static bool IsEligible(AcceptanceManifestCheck? check) =>
        check?.Type?.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase) == true &&
        DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(check.Project);
}
