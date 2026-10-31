using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeclaredTestEvidenceSelectors
{
    internal static IReadOnlyList<string> FromDeveloperReport(string testsField,
        IReadOnlyList<DeclaredTestReportRows.Row>? rows = null)
    {
        const string prefix = "deferred - ";
        var field = testsField.Trim();
        if (field.StartsWith("tests:", StringComparison.OrdinalIgnoreCase)) field = field[6..].Trim();
        if (!field.StartsWith(prefix, StringComparison.Ordinal)) return [];
        return field[prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Trim('`')).Where(name => name.Length > 0).Select(name =>
            {
                var identities = (rows ?? []).Where(row => row.Identity == name || row.Name == name ||
                    row.Identity.EndsWith("::" + name, StringComparison.Ordinal))
                    .Select(row => row.Identity).Distinct(StringComparer.Ordinal).ToArray();
                return identities.Length == 1 ? identities[0] : name;
            }).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static DeveloperDeferredTestSelection? ForProject(string worktree, string testsField)
    {
        try
        {
            var path = AcceptanceManifestLocator.Resolve(worktree, Path.Combine(worktree, ".orchestrator"));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var checks = document.RootElement.GetProperty("checks")
                .Deserialize<GoalAcceptanceVerifier.AcceptanceManifestCheck[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var check = checks?.SingleOrDefault(DeclaredTestReportRows.IsDeclaredCheck);
            if (check is null) return null;
            // At request creation no candidate-bound report is available. Keep the names literal.
            return new(FromDeveloperReport(testsField).Select(name =>
                new FindingEvidenceSelection(check.Project ?? ".", name)).ToArray(), []);
        }
        catch (Exception) { return null; }
    }
}
