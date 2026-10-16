using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Pure text reach estimates. These results never authorize verdict reuse.
internal static class AcceptanceLaneCarryShadowReach
{
    private static readonly Regex Identifiers = new("[A-Za-z0-9_]+", RegexOptions.Compiled);
    private static readonly string[] Markers = ["ProcessStartInfo", "pwsh", "powershell", "EnumerateFiles", "GetFiles"];
    private static readonly string[] MarkerRoots = ["src/", "scripts/", "docs/", "config/"];

    internal sealed record Source(string[] Types, HashSet<string> Identifiers);
    internal sealed record ChangeSet(HashSet<string> Paths, bool ProjectChanged, bool MarkerEligible,
        HashSet<string> FirstHop, HashSet<string> SecondHop, string[] FileNames, string[] CsFileNames);
    internal sealed record Result(bool Optimistic, bool Mid, bool Conservative);

    internal static Source IndexSource(string text) => new(
        CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Select(node => node.Identifier.ValueText).Distinct(StringComparer.Ordinal).ToArray(),
        Tokens(text));

    internal static bool IsSource(string path) => Normalize(path).StartsWith("src/", StringComparison.Ordinal) &&
        Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    internal static ChangeSet Prepare(IReadOnlyList<string> changedFiles, IReadOnlyDictionary<string, Source> sources)
    {
        var paths = changedFiles.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var first = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.Where(IsSource))
        {
            if (sources.TryGetValue(path, out var source)) first.UnionWith(source.Types);
            else first.Add(Path.GetFileNameWithoutExtension(path));
        }
        var second = new HashSet<string>(StringComparer.Ordinal);
        if (first.Count > 0)
            foreach (var source in sources.Values)
                if (source.Identifiers.Overlaps(first)) second.UnionWith(source.Types);
        return new ChangeSet(paths, paths.Any(IsProject),
            paths.Any(path => MarkerRoots
                .Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))),
            first, second,
            paths.Where(path => !IsCs(path)).Select(Path.GetFileNameWithoutExtension).ToArray()!,
            paths.Where(path => IsCs(path) && !IsSource(path)).Select(Path.GetFileNameWithoutExtension).ToArray()!);
    }

    internal static Result Evaluate(ChangeSet changes, IReadOnlyList<string> lanePaths, string laneText,
        bool unresolved = false)
    {
        if (unresolved || changes.ProjectChanged || lanePaths.Any(path => changes.Paths.Contains(Normalize(path))))
            return new(true, true, true);
        var tokens = Tokens(laneText);
        var optimistic = tokens.Overlaps(changes.FirstHop) || changes.CsFileNames.Any(tokens.Contains) ||
            changes.FileNames.Any(name => laneText.Contains(name, StringComparison.Ordinal));
        var mid = optimistic || tokens.Overlaps(changes.SecondHop);
        return new(optimistic, mid, mid || changes.MarkerEligible &&
            Markers.Any(marker => laneText.Contains(marker, StringComparison.Ordinal)));
    }

    private static HashSet<string> Tokens(string text) => Identifiers.Matches(text)
        .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static bool IsCs(string path) => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);
    private static bool IsProject(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".props" or ".sln" ||
        Path.GetFileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase);
}
