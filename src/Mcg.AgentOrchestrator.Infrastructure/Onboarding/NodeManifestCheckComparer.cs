using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Compares learned Node invocations with command and whole-suite crosswalk checks.</summary>
internal static class NodeManifestCheckComparer
{
    internal static IReadOnlyList<ManifestCheckComparison> Compare(ProjectModel model, string manifestJson)
    {
        var (learned, blockers) = NodeVerificationChecks.For(model);
        var manifest = ReadChecks(manifestJson);
        if (blockers.Count > 0)
            return blockers.Select(blocker => new ManifestCheckComparison(blocker.FactKey,
                ManifestCheckResultKind.Unresolved, null, null, blocker.Reason)).ToArray();

        var results = new List<ManifestCheckComparison>();
        var used = new HashSet<NodeVerificationChecks.Check>();
        foreach (var check in learned)
        {
            var key = Identity(check);
            var candidates = manifest.Where(item => Identity(item) == key).ToArray();
            if (candidates.Length == 0)
                candidates = manifest.Where(item => item.Name == check.Name).ToArray();
            if (candidates.Length == 0)
            {
                results.Add(new(key, ManifestCheckResultKind.MissingFromManifest, Invocation(check), null,
                    $"No manifest Node check runs {key}."));
                continue;
            }
            used.UnionWith(candidates);
            var differing = candidates.FirstOrDefault(item => !Equivalent(check, item));
            var matches = differing is null;
            results.Add(new(key, matches ? ManifestCheckResultKind.Match : ManifestCheckResultKind.RunnerDiffers,
                Invocation(check), Invocation(differing ?? candidates[0]),
                matches ? "" : "The learned and manifest Node invocations differ."));
        }
        foreach (var check in manifest.Where(check => !used.Contains(check)).DistinctBy(Identity))
            results.Add(new(Identity(check), ManifestCheckResultKind.MissingFromLearned, null, Invocation(check),
                "No learned Node check declares this invocation."));
        return results.OrderBy(result => result.ProjectPath, StringComparer.Ordinal).ToArray();
    }

    private static string Identity(NodeVerificationChecks.Check check) => check.Type == "crosswalk-test"
        ? $"crosswalk-test {NormalizeProject(check.Project)}"
        : $"{check.Command} {check.Arguments.FirstOrDefault()}".TrimEnd();

    private static bool Equivalent(NodeVerificationChecks.Check learned, NodeVerificationChecks.Check manifest) =>
        learned.Type == manifest.Type && learned.Command == manifest.Command &&
        (learned.Type != "crosswalk-test" || NormalizeProject(learned.Project) == NormalizeProject(manifest.Project)) &&
        learned.Arguments.SequenceEqual(manifest.Arguments, StringComparer.Ordinal);

    private static string Invocation(NodeVerificationChecks.Check check) => string.Join(" ",
        new[] { check.Type == "crosswalk-test" ? $"crosswalk-test {NormalizeProject(check.Project)}" : check.Command }
            .Concat(check.Arguments));

    private static string NormalizeProject(string? project)
    {
        var path = (project ?? "").Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        path = path.TrimEnd('/');
        return path.Length == 0 ? "." : path;
    }

    private static IReadOnlyList<NodeVerificationChecks.Check> ReadChecks(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The manifest must be an object.");
        var result = new List<NodeVerificationChecks.Check>();
        if (!TryProperty(root, "checks", out var checks))
            return result;
        if (checks.ValueKind != JsonValueKind.Array)
            throw new JsonException("Manifest checks must be an array.");
        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object)
                throw new JsonException("Each manifest check must be an object.");
            if (TryProperty(check, "type", out var declaredType) && declaredType.ValueKind != JsonValueKind.String)
                continue;
            var name = ReadString(check, "name") ?? "";
            var type = (ReadString(check, "type") ?? "command").ToLowerInvariant();
            var command = ReadString(check, "command");
            if (type != "crosswalk-test" && !(type == "command" && command is "npm" or "npx") &&
                !NodeVerificationChecks.Entries.Any(entry => entry.Name == name))
                continue;
            if (type == "crosswalk-test" && TryProperty(check, "project", out var project) && project.ValueKind != JsonValueKind.String)
                continue;
            string[] arguments = [];
            if (TryProperty(check, "arguments", out var declaredArguments))
            {
                if (declaredArguments.ValueKind != JsonValueKind.Array)
                    continue;
                var values = declaredArguments.EnumerateArray().ToArray();
                if (values.Any(value => value.ValueKind != JsonValueKind.String))
                    continue;
                arguments = values.Select(value => value.GetString()!).ToArray();
            }
            result.Add(new(name, type, command, ReadString(check, "project"), arguments));
        }
        return result;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}
