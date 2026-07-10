using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class StateEffectProposalKinds
{
    public const string BacklogAdd = "backlog-add";
    public const string BacklogClose = "backlog-close";
    public const string GoalRecordNote = "goal-record-note";

    public static bool IsKnown(string kind) =>
        kind.Equals(BacklogAdd, StringComparison.Ordinal) ||
        kind.Equals(BacklogClose, StringComparison.Ordinal) ||
        kind.Equals(GoalRecordNote, StringComparison.Ordinal);
}

public sealed record StateEffectProposal(
    string RelativePath,
    string Kind,
    string Slug,
    string Hash,
    IReadOnlyDictionary<string, string> Fields,
    string Body);

public sealed record StateEffectProposalValidationResult(
    bool Passed,
    IReadOnlyList<StateEffectProposal> Proposals,
    IReadOnlyList<string> Errors)
{
    public string Summary =>
        Passed
            ? Proposals.Count == 0 ? "no state-effect proposals present" : $"validated {Proposals.Count} state-effect proposal(s)"
            : string.Join(Environment.NewLine, Errors);
}

public static partial class StateEffectProposalParser
{
    private const string DirectoryName = ".orchestrator-proposals";
    private static readonly string[] KnownKinds =
    [
        StateEffectProposalKinds.GoalRecordNote,
        StateEffectProposalKinds.BacklogClose,
        StateEffectProposalKinds.BacklogAdd
    ];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public static bool IsProposalPath(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return normalized.StartsWith(DirectoryName + "/", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    public static StateEffectProposalValidationResult ValidateDirectory(
        string rootPath,
        IReadOnlyList<string>? changedFiles = null)
    {
        var paths = ResolveProposalPaths(rootPath, changedFiles);
        var proposals = new List<StateEffectProposal>();
        var errors = new List<string>();

        foreach (var relativePath in paths)
        {
            var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                errors.Add($"{relativePath}: proposal file not found");
                continue;
            }

            var parsed = TryParse(rootPath, relativePath, out var proposal, out var error);
            if (!parsed)
            {
                errors.Add(error ?? $"{relativePath}: invalid proposal");
                continue;
            }

            proposals.Add(proposal!);
        }

        return new StateEffectProposalValidationResult(errors.Count == 0, proposals, errors);
    }

    public static bool TryParse(
        string rootPath,
        string relativePath,
        out StateEffectProposal? proposal,
        out string? error)
    {
        proposal = null;
        error = null;

        var normalizedPath = NormalizePath(relativePath);
        if (!TryParseFileName(normalizedPath, out var fileKind, out var slug, out error))
        {
            return false;
        }

        var fullPath = Path.Combine(rootPath, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
        var content = File.ReadAllText(fullPath);
        if (!TrySplitFrontMatter(content, out var fields, out var body, out error))
        {
            error = $"{normalizedPath}: {error}";
            return false;
        }

        if (!RequireField(fields, "kind", normalizedPath, out var kind, out error))
        {
            return false;
        }

        if (!kind.Equals(fileKind, StringComparison.Ordinal))
        {
            error = $"{normalizedPath}: kind '{kind}' must match filename kind '{fileKind}'";
            return false;
        }

        if (!ValidateFields(normalizedPath, kind, fields, body, out error))
        {
            return false;
        }

        proposal = new StateEffectProposal(
            normalizedPath,
            kind,
            slug,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            fields,
            body.Trim());
        return true;
    }

    private static IReadOnlyList<string> ResolveProposalPaths(string rootPath, IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is { Count: > 0 })
        {
            return changedFiles
                .Select(NormalizePath)
                .Where(IsProposalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var directory = Path.Combine(rootPath, DirectoryName);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => NormalizePath(Path.GetRelativePath(rootPath, path)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParseFileName(string relativePath, out string kind, out string slug, out string? error)
    {
        kind = string.Empty;
        slug = string.Empty;
        error = null;

        if (!IsProposalPath(relativePath))
        {
            error = $"{relativePath}: proposal path must be {DirectoryName}/<kind>-<slug>.md";
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        foreach (var knownKind in KnownKinds)
        {
            var prefix = knownKind + "-";
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            kind = knownKind;
            slug = fileName[prefix.Length..];
            if (!SlugPattern().IsMatch(slug))
            {
                error = $"{relativePath}: proposal slug must match [a-z0-9][a-z0-9-]*";
                return false;
            }

            return true;
        }

        error = $"{relativePath}: proposal kind must be one of {string.Join(", ", KnownKinds.Order())}";
        return false;
    }

    private static bool TrySplitFrontMatter(
        string content,
        out Dictionary<string, string> fields,
        out string body,
        out string? error)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = string.Empty;
        error = null;

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            error = "proposal must start with YAML-style front matter";
            return false;
        }

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            error = "proposal front matter must end with ---";
            return false;
        }

        var frontMatter = normalized[4..end];
        body = normalized[(end + "\n---\n".Length)..];
        foreach (var rawLine in frontMatter.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                error = $"front matter line must be key: value: {line}";
                return false;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
            {
                error = $"front matter line must include key and value: {line}";
                return false;
            }

            fields[key] = value;
        }

        return true;
    }

    private static bool ValidateFields(
        string relativePath,
        string kind,
        IReadOnlyDictionary<string, string> fields,
        string body,
        out string? error)
    {
        error = null;
        if (kind.Equals(StateEffectProposalKinds.BacklogAdd, StringComparison.Ordinal))
        {
            if (!RequireField(fields, "title", relativePath, out _, out error))
            {
                return false;
            }

            return true;
        }

        if (kind.Equals(StateEffectProposalKinds.BacklogClose, StringComparison.Ordinal))
        {
            if (!RequireField(fields, "id", relativePath, out _, out error))
            {
                return false;
            }

            return true;
        }

        if (kind.Equals(StateEffectProposalKinds.GoalRecordNote, StringComparison.Ordinal))
        {
            if (fields.TryGetValue("message", out var message) && !string.IsNullOrWhiteSpace(message))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(body))
            {
                return true;
            }

            error = $"{relativePath}: goal-record-note requires message or body";
            return false;
        }

        error = $"{relativePath}: unknown proposal kind '{kind}'";
        return false;
    }

    private static bool RequireField(
        IReadOnlyDictionary<string, string> fields,
        string name,
        string relativePath,
        out string value,
        out string? error)
    {
        if (fields.TryGetValue(name, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            error = null;
            return true;
        }

        value = string.Empty;
        error = $"{relativePath}: missing required field '{name}'";
        return false;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
