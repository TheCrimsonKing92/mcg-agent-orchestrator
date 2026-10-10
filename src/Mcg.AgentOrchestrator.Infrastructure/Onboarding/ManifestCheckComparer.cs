using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Compares only the project and runner of declared dotnet-test checks.</summary>
public static class ManifestCheckComparer
{
    public static IReadOnlyList<ManifestCheckComparison> Compare(ProjectModel model, string manifestJson)
    {
        var (checks, unresolved) = LearnedTestCheckDeriver.Derive(model);
        return Compare(checks, unresolved, manifestJson);
    }

    public static IReadOnlyList<ManifestCheckComparison> Compare(IReadOnlyList<LearnedTestCheck> checks,
        IReadOnlyList<UnresolvedTestUnit> unresolved, string manifestJson)
    {
        var learned = checks.ToDictionary(check => NormalizePath(check.ProjectPath), StringComparer.OrdinalIgnoreCase);
        var pending = unresolved.ToDictionary(unit => NormalizePath(unit.UnitId), StringComparer.OrdinalIgnoreCase);
        var manifest = ReadChecks(manifestJson);
        var results = new List<ManifestCheckComparison>();
        foreach (var (path, declaration) in manifest)
        {
            ManifestCheckComparison comparison;
            if (learned.TryGetValue(path, out var check))
            {
                var matches = string.Equals(check.Runner, declaration.Runner, StringComparison.OrdinalIgnoreCase);
                comparison = new(check.ProjectPath, matches ? ManifestCheckResultKind.Match : ManifestCheckResultKind.RunnerDiffers,
                    check.Runner, declaration.Runner, matches ? "" : "The learned and manifest runners differ.");
            }
            else if (pending.TryGetValue(path, out var unit))
            {
                comparison = new(NormalizePath(unit.UnitId), ManifestCheckResultKind.Unresolved, null, declaration.Runner,
                    $"Runner requires owner answer: {unit.FactKey}");
            }
            else
            {
                comparison = new(path, ManifestCheckResultKind.MissingFromLearned, null, declaration.Runner,
                    "No learned test check names this project.");
            }

            if (declaration.ConflictingDuplicate)
                comparison = comparison with { Reason = (comparison.Reason + " Later duplicate declares a different runner; first declaration used.").Trim() };
            results.Add(comparison);
        }

        foreach (var (path, check) in learned)
        {
            if (!manifest.ContainsKey(path))
                results.Add(new(check.ProjectPath, ManifestCheckResultKind.MissingFromManifest, check.Runner, null,
                    "No manifest dotnet-test check names this project."));
        }

        return results.OrderBy(result => result.ProjectPath, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, ManifestCheck> ReadChecks(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The manifest must be an object.");
        var result = new Dictionary<string, ManifestCheck>(StringComparer.OrdinalIgnoreCase);
        if (!TryProperty(root, "checks", out var checks))
            return result;
        if (checks.ValueKind != JsonValueKind.Array)
            throw new JsonException("Manifest checks must be an array.");
        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object)
                throw new JsonException("Each manifest check must be an object.");
            if (!TryProperty(check, "type", out var type) || type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "dotnet-test", StringComparison.OrdinalIgnoreCase) ||
                !TryProperty(check, "project", out var project) || project.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(project.GetString()))
                continue;
            var path = NormalizePath(project.GetString()!);
            var runner = "vstest";
            if (TryProperty(check, "runner", out var runnerValue) && runnerValue.ValueKind != JsonValueKind.Null)
            {
                if (runnerValue.ValueKind != JsonValueKind.String)
                    throw new JsonException("A manifest runner must be a string or null.");
                if (!string.IsNullOrWhiteSpace(runnerValue.GetString()))
                    runner = runnerValue.GetString()!.Trim().ToLowerInvariant();
            }

            if (result.TryGetValue(path, out var existing))
                result[path] = existing with { ConflictingDuplicate = existing.ConflictingDuplicate || existing.Runner != runner };
            else
                result.Add(path, new ManifestCheck(runner, false));
        }

        return result;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }

    private sealed record ManifestCheck(string Runner, bool ConflictingDuplicate);
}
