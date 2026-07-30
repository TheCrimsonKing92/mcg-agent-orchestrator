namespace Mcg.AgentOrchestrator.Core;

public enum PathOverlapKind
{
    None,
    Exact,
    DirectoryPrefix
}

public static class RepositoryPathOverlap
{
    public static string Normalize(string path)
    {
        return path.Replace('\\', '/').Trim('/').Trim();
    }

    public static PathOverlapKind Classify(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            return PathOverlapKind.Exact;
        }

        return a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)
            ? PathOverlapKind.DirectoryPrefix
            : PathOverlapKind.None;
    }
}
