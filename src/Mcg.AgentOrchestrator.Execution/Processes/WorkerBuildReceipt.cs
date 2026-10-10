using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record WorkerBuildReceiptVerdict(
    bool Matches, string Reason, IReadOnlyList<string>? Projects = null);

internal static class WorkerBuildReceipt
{
    internal const string FileName = "worker-build-receipt.json";

    internal static WorkerBuildReceiptVerdict Evaluate(string path, string worktreeRoot)
    {
        if (!File.Exists(path)) return new(false, "receipt-missing");
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(false, "receipt-unreadable"); }
        catch (JsonException) { return new(false, "receipt-malformed"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryNumber(root, "schemaVersion", out var version)) return new(false, "receipt-malformed");
            if (version != 1) return new(false, "schema-unknown");
            if (!TryString(root, "treeDigest", out var recordedDigest) ||
                !TryString(root, "digestAlgorithm", out var algorithm) ||
                !TryString(root, "buildOutcome", out var outcome) ||
                !TryString(root, "generatedAtUtc", out var generatedAt) ||
                !TryString(root, "worktreeRoot", out var recordedRoot) ||
                !TryString(root, "configuration", out _) ||
                !root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array ||
                !projects.EnumerateArray().All(project => project.ValueKind == JsonValueKind.String) ||
                !DateTimeOffset.TryParse(generatedAt, out _) ||
                algorithm != WorktreeTreeDigest.Algorithm ||
                !recordedDigest.StartsWith(algorithm + ":", StringComparison.Ordinal)) return new(false, "receipt-malformed");
            if (outcome != "success") return new(false, outcome == "failure" ? "build-failed" : "receipt-malformed");
            try
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(Path.GetFullPath(recordedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(worktreeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison))
                    return new(false, "worktree-mismatch");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            { return new(false, "receipt-malformed"); }
            if (!WorktreeTreeDigest.TryCompute(worktreeRoot, out var currentDigest, out _)) return new(false, "digest-unavailable");
            return string.Equals(recordedDigest, currentDigest, StringComparison.Ordinal)
                ? new(true, "matched", projects.EnumerateArray().Select(project => project.GetString()!).ToArray())
                : new(false, "digest-mismatch");
        }
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryNumber(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
