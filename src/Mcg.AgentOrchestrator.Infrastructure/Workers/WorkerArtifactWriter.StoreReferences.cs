using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class WorkerArtifactWriter
{
    private static IReadOnlyList<StoreReferenceOutcome> WriteStoreReferences(
        string contextDirectory, IEnumerable<string> sources, string? storeRoot, IClock clock)
    {
        var directory = Path.Combine(contextDirectory, "store-refs");
        DeleteStoreReferenceDirectory(directory);
        var references = WorkerStoreReferenceResolver.Parse(sources);
        var outcomes = new List<StoreReferenceOutcome>();
        var totalChars = 0;
        for (var index = 0; index < references.Count; index++)
        {
            var reference = references[index];
            var resolution = WorkerStoreReferenceResolver.Resolve(reference, storeRoot, clock, out var excerptLength);
            if (resolution.ReasonCode is null && (index >= WorkerStoreReferenceResolver.MaxReferencesPerDispatch
                || totalChars + excerptLength > WorkerStoreReferenceResolver.MaxTotalExcerptChars))
                resolution = new(null, "limit-exceeded");
            if (resolution.ReasonCode is not null)
            {
                outcomes.Add(new(reference.Name, null, resolution.ReasonCode));
                continue;
            }
            var relativePath = $"store-refs/{reference.Name}.md";
            try
            {
                Directory.CreateDirectory(directory);
                WriteText(Path.Combine(contextDirectory, relativePath), resolution.Content!);
                totalChars += excerptLength;
                outcomes.Add(new(reference.Name, relativePath, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                try { File.Delete(Path.Combine(contextDirectory, relativePath)); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException or ArgumentException) { }
                outcomes.Add(new(reference.Name, null, "read-failed"));
            }
        }
        return outcomes;
    }

    private static void AppendStoreReferenceManifest(List<string> lines, IReadOnlyList<StoreReferenceOutcome>? outcomes)
    {
        if (outcomes is not { Count: > 0 }) return;
        lines.Add(string.Empty);
        lines.Add("## Store References");
        lines.AddRange(outcomes.Where(outcome => outcome.Resolved)
            .Select(outcome => $"- {outcome.RelativePath}: bounded untrusted orchestrator record with provenance."));
        var unresolved = outcomes.Where(outcome => !outcome.Resolved).ToArray();
        if (unresolved.Length == 0) return;
        lines.Add(string.Empty);
        lines.Add("## Context Diagnostics");
        lines.AddRange(unresolved.Select(outcome => $"STORE_REF_UNRESOLVED name={outcome.Name} reason={outcome.ReasonCode}"));
    }

    private static void SnapshotStoreReferences(string contextDirectory, string packageDirectory)
    {
        var destination = Path.Combine(packageDirectory, "store-refs");
        DeleteStoreReferenceDirectory(destination);
        var source = Path.Combine(contextDirectory, "store-refs");
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.md"))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    private static void DeleteStoreReferenceDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0);
    }
}
