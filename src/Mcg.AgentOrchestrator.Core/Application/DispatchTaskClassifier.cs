using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static class DispatchTaskClassifier
{
    private static readonly string[] PureMoveMarkers =
        ["pure move", "pure-move", "behavior-preserving", "decomposition slice"];

    public static DispatchTaskClass Classify(string? briefText, IEnumerable<string>? changedPaths = null)
    {
        var brief = briefText ?? string.Empty;
        var citedPaths = Regex.Matches(brief, "`([^`\r\n]+)`")
            .Select(match => match.Groups[1].Value);
        var paths = citedPaths.Concat(changedPaths ?? [])
            .Select(NormalizePath).Where(path => path is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (PostLandingCanaryTrigger.Evaluate(paths).ShouldRun)
            return DispatchTaskClass.GateEngine;
        if (PureMoveMarkers.Any(marker => brief.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return DispatchTaskClass.PureMove;
        if (paths.Length > 0 && paths.All(path =>
                path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            return DispatchTaskClass.DocsOnly;
        return DispatchTaskClass.Other;
    }

    public static string WireName(DispatchTaskClass taskClass) => taskClass switch
    {
        DispatchTaskClass.GateEngine => "gate-engine",
        DispatchTaskClass.PureMove => "pure-move",
        DispatchTaskClass.DocsOnly => "docs-only",
        DispatchTaskClass.Other => "other",
        _ => "unrecorded"
    };

    private static string? NormalizePath(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) ||
            token.IndexOfAny(['<', '>', '*', '=']) >= 0)
            return null;
        var path = token.Replace('\\', '/');
        var anchor = path.IndexOfAny(['#']);
        var symbol = path.IndexOf("::", StringComparison.Ordinal);
        if (symbol >= 0) path = path[..symbol];
        if (anchor >= 0 && anchor < path.Length) path = path[..anchor];
        var line = path.LastIndexOf(':');
        if (line >= 0 && int.TryParse(path[(line + 1)..], out _)) path = path[..line];
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.StartsWith('/') || path.Contains(':') ||
            path.Split('/').Any(part => part is ".." or ".") ||
            (!path.Contains('/') && !Path.HasExtension(path)))
            return null;
        return path;
    }
}
