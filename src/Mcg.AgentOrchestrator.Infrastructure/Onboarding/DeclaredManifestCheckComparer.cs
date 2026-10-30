using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DeclaredManifestCheckComparer
{
    internal static IReadOnlyList<ManifestCheckComparison> Compare(ProjectModel model, string manifestJson, AppliedOwnerAnswers applied)
    {
        var (learned, blockers) = DeclaredProjectDiscoveryAdapter.For(model, applied);
        if (blockers.Count > 0)
            return blockers.Select(blocker => new ManifestCheckComparison(blocker.FactKey,
                ManifestCheckResultKind.Unresolved, null, null, blocker.Reason)).ToArray();
        using var document = JsonDocument.Parse(manifestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The manifest must be an object.");
        var declarations = document.RootElement.Deserialize<CheckManifest>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var manifest = declarations.Checks.Where(check => check.Type.Equals("junit-command", StringComparison.OrdinalIgnoreCase) ||
            check.Name is DeclaredProjectDiscoveryAdapter.BuildCheckName or DeclaredProjectDiscoveryAdapter.TestCheckName).ToArray();
        var results = new List<ManifestCheckComparison>();
        foreach (var check in learned)
        {
            var matches = manifest.Where(item => item.Name == check.Name).ToArray();
            results.Add(matches.Length == 0
                ? new(check.Name, ManifestCheckResultKind.MissingFromManifest, Invocation(check), null, "No manifest check has this declared name.")
                : new(check.Name, matches.Length == 1 && Equivalent(check, matches[0]) ? ManifestCheckResultKind.Match : ManifestCheckResultKind.RunnerDiffers,
                    Invocation(check), Invocation(matches[0]), matches.Length == 1 && Equivalent(check, matches[0]) ? "" : "The declared and manifest invocations differ."));
        }
        foreach (var check in manifest.Where(item => learned.All(check => check.Name != item.Name)))
            results.Add(new(check.Name, ManifestCheckResultKind.MissingFromLearned, null, Invocation(check), "No owner declaration names this check."));
        return results.OrderBy(result => result.ProjectPath, StringComparer.Ordinal).ToArray();
    }

    private static bool Equivalent(Check left, Check right) =>
        left.Type.Equals(right.Type, StringComparison.OrdinalIgnoreCase) && left.Name == right.Name && left.Command == right.Command &&
        Project(left.Project) == Project(right.Project) && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
        left.Reports.Select(Normalize).ToHashSet(StringComparer.Ordinal).SetEquals(right.Reports.Select(Normalize));

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Project(string? path)
    {
        var value = Normalize(path ?? ".");
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value.TrimEnd('/') is "" ? "." : value.TrimEnd('/');
    }
    private static string Invocation(Check check) => $"{check.Type} {check.Command} {string.Join(" ", check.Arguments)} project={Project(check.Project)} reports={string.Join(",", check.Reports)}";

    private sealed class CheckManifest
    {
        public IReadOnlyList<Check> Checks { get; init; } = [];
    }
}
