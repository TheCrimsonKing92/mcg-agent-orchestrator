using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class AdjudicationEvidenceResolver
{
    public bool TryResolveForLesson(string reference, string workingDirectory, Goal? goal,
        out EvidenceManifestEntry entry, out string rejection)
    {
        entry = new EvidenceManifestEntry(reference, string.Empty);
        rejection = $"evidence-reference-unresolved {reference}";
        var colon = reference.IndexOf(':');
        var kind = colon > 0 ? reference[..colon].ToLowerInvariant() : string.Empty;
        var value = colon > 0 ? reference[(colon + 1)..] : string.Empty;
        if (kind is "focused-evidence" or "acceptance-attempt")
        {
            if (goal is null)
            {
                rejection = $"evidence-reference-requires-goal {reference}";
                return false;
            }
            return TryResolve(reference, goal,
                new AdjudicateOperatorIntentPayload("lesson", "", [], 0, workingDirectory), out entry);
        }
        if (kind is not ("operator-evidence" or "trx"))
        {
            rejection = $"evidence-reference-free-text-refused {reference}";
            return false;
        }
        try
        {
            if (!TryResolveFile(reference, kind, value, workingDirectory,
                    allowLineSuffix: true, out entry)) return false;
            rejection = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool TryResolveFile(string reference, string kind, string value,
        string workingDirectory, bool allowLineSuffix, out EvidenceManifestEntry entry)
    {
        entry = new EvidenceManifestEntry(reference, string.Empty);
        if (string.IsNullOrWhiteSpace(value)) return false;
        string? path;
        try { path = ResolvePath(value, workingDirectory); }
        catch (ArgumentException) when (kind == "operator-evidence" && allowLineSuffix)
        {
            path = null;
        }
        if (kind == "operator-evidence" && allowLineSuffix && (path is null || !File.Exists(path)))
        {
            var lastColon = value.LastIndexOf(':');
            if (lastColon > 0 && int.TryParse(value[(lastColon + 1)..], out var line))
            {
                path = ResolvePath(value[..lastColon], workingDirectory);
                if (line < 1 || !File.Exists(path) || File.ReadLines(path).Take(line).Count() < line)
                    return false;
            }
        }
        if (path is null) return false;
        if (kind == "trx" && !path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(path)) return false;
        if (kind == "trx" && XDocument.Load(path).Root?.Name.LocalName != "TestRun") return false;
        entry = new EvidenceManifestEntry(reference, Hash(File.ReadAllBytes(path)));
        return true;
    }

    private static string ResolvePath(string value, string workingDirectory) =>
        Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(value, workingDirectory);
}
