namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerAcceptanceManifestDisplayPath
{
    public static string? ForPrompt(WorkerTargetHome? targetHome, string worktreePath)
    {
        var manifestPath = targetHome?.AcceptanceManifestPath?.Invoke(worktreePath);
        return string.IsNullOrWhiteSpace(manifestPath) ? null : Render(worktreePath, manifestPath);
    }

    public static string Render(string worktreePath, string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(worktreePath), fullPath);
        var escapesWorktree = relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
        return !Path.IsPathRooted(relativePath) && relativePath != "." && !escapesWorktree
            ? relativePath.Replace('\\', '/')
            : fullPath;
    }
}
