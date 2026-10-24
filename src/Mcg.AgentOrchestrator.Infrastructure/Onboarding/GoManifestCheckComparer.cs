using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Compares the fixed Go verification set with declared command argument vectors.</summary>
internal static class GoManifestCheckComparer
{
    internal static IReadOnlyList<ManifestCheckComparison> Compare(
        IReadOnlyList<UnresolvedTestUnit> unresolved, string manifestJson)
    {
        var learned = GoVerificationChecks.Entries.ToDictionary(check => check.Arguments[0], StringComparer.Ordinal);
        var manifest = ReadChecks(manifestJson);
        var results = new List<ManifestCheckComparison>();
        foreach (var (key, arguments) in manifest)
        {
            var path = $"go {key}";
            var manifestRunner = Invocation(GoVerificationChecks.Command, arguments);
            if (learned.TryGetValue(key, out var check))
            {
                var matches = check.Arguments.SequenceEqual(arguments, StringComparer.Ordinal);
                results.Add(new(path, matches ? ManifestCheckResultKind.Match : ManifestCheckResultKind.RunnerDiffers,
                    Invocation(check.Command, check.Arguments), manifestRunner,
                    matches ? "" : "The learned and manifest go arguments differ."));
            }
            else
                results.Add(new(path, ManifestCheckResultKind.MissingFromLearned, null, manifestRunner,
                    $"No learned go check runs {path}."));
        }

        foreach (var (key, check) in learned)
            if (!manifest.ContainsKey(key))
                results.Add(new($"go {key}", ManifestCheckResultKind.MissingFromManifest,
                    Invocation(check.Command, check.Arguments), null,
                    $"No manifest go command check runs go {key}."));
        foreach (var unit in unresolved)
            results.Add(new(unit.UnitId, ManifestCheckResultKind.Unresolved, null, null,
                $"Runner requires owner answer: {unit.FactKey}"));

        return results.OrderBy(result => result.ProjectPath, StringComparer.Ordinal).ToArray();
    }

    private static string Invocation(string command, IReadOnlyList<string> arguments) =>
        string.Join(" ", new[] { command }.Concat(arguments));

    private static Dictionary<string, string[]> ReadChecks(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The manifest must be an object.");
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!TryProperty(root, "checks", out var checks))
            return result;
        if (checks.ValueKind != JsonValueKind.Array)
            throw new JsonException("Manifest checks must be an array.");
        foreach (var check in checks.EnumerateArray())
        {
            if (check.ValueKind != JsonValueKind.Object)
                throw new JsonException("Each manifest check must be an object.");
            if (TryProperty(check, "type", out var type) &&
                (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "command", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!TryProperty(check, "command", out var command) || command.ValueKind != JsonValueKind.String ||
                command.GetString() != GoVerificationChecks.Command ||
                !TryProperty(check, "arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array)
                continue;
            var values = arguments.EnumerateArray().ToArray();
            if (values.Length == 0 || values.Any(value => value.ValueKind != JsonValueKind.String))
                continue;
            var tokens = values.Select(value => value.GetString()!).ToArray();
            if (tokens[0].Length > 0)
                result.TryAdd(tokens[0], tokens);
        }

        return result;
    }

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
