using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainReproductionEvidenceCheck
{
    internal static string? InvalidReason(MergeTrainReproductionProvenance? evidence,
        MergeTrainMemberBinding member, string mainRevision)
    {
        if (evidence?.ValidationFailure is { } failure) return failure;
        if (evidence is null || string.IsNullOrWhiteSpace(evidence.TreeRevision) ||
            string.IsNullOrWhiteSpace(evidence.ExpectedTreeRevision) ||
            string.IsNullOrWhiteSpace(evidence.PartitionBuildOutputRoot) ||
            evidence.BuildOutputRoots.Count == 0 || evidence.AssemblyPaths.Count == 0)
            return "missing-provenance";
        if (evidence.MainRevision != mainRevision || evidence.MemberRevision != member.CandidateRevision ||
            evidence.TreeRevision != evidence.ExpectedTreeRevision) return "tree-mismatch";
        try
        {
            var root = Normalize(evidence.PartitionBuildOutputRoot);
            if (evidence.BuildOutputRoots.Any(path => !Normalize(path).Equals(root, Comparison)))
                return "root-mismatch";
            if (evidence.AssemblyPaths.Any(path => !Normalize(path).StartsWith(root + Path.DirectorySeparatorChar, Comparison)))
                return "assembly-outside-root";
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return "invalid-path"; }
    }

    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Provenance paths must be absolute.");
        // Resolve existing junctions/symlinks as well as lexical '..' components.
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            root = Path.Combine(root, part);
            FileSystemInfo info = Directory.Exists(root) ? new DirectoryInfo(root) : new FileInfo(root);
            if (info.Exists && info.LinkTarget is not null)
                root = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Unresolved provenance link.");
        }
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
