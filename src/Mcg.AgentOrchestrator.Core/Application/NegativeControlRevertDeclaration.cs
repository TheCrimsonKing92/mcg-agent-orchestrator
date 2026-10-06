namespace Mcg.AgentOrchestrator.Core;

// A declaration authorizes only explicit non-source files; rejection authorizes nothing.
public sealed record NegativeControlRevertDeclaration(bool Declared, IReadOnlyList<string> Paths, string? Rejection)
{
    private const string Prefix = "negative-control-revert:";

    public static NegativeControlRevertDeclaration Parse(string briefText)
    {
        var lines = briefText.Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
        if (lines.Length == 0) return new(false, [], null);
        if (lines.Length > 1) return Rejected("declaration-duplicate");
        var value = lines[0][Prefix.Length..].Trim();
        if (value.Length == 0) return Rejected("declaration-empty");
        var paths = value.Split(',').Select(path => path.Trim().Replace('\\', '/')).ToArray();
        if (paths.Any(path => path.StartsWith('/') || path.Contains(':') ||
            path.IndexOfAny(['*', '?', '[']) >= 0 || path.Split('/').Any(part => part is "" or "." or "..")))
            return Rejected("declaration-path-invalid");
        if (paths.Any(path => !path.StartsWith("tests/", StringComparison.Ordinal) &&
            !path.StartsWith("config/", StringComparison.Ordinal) && !path.StartsWith(".agents/", StringComparison.Ordinal)))
            return Rejected("declaration-root-not-allowed");
        return new(true, FindingEvidenceRevertPaths.Canonicalize(paths), null);
    }

    private static NegativeControlRevertDeclaration Rejected(string code) => new(true, [], code);
}
