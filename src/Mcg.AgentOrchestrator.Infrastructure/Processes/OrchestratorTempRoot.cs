namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Places orchestrator-owned temporary artifacts below the scanner-excluded mcg-run root.</summary>
public static class OrchestratorTempRoot
{
    public static string GetParent(string? tempPath = null)
    {
        var parent = Path.Combine(tempPath ?? Path.GetTempPath(), "mcg-run", "tmp");
        Directory.CreateDirectory(parent);
        return parent;
    }

    public static string GetPurposeDirectory(string purpose, string? tempPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (purpose is "." or ".." || Path.IsPathRooted(purpose) ||
            purpose.Contains(Path.DirectorySeparatorChar) || purpose.Contains(Path.AltDirectorySeparatorChar) ||
            purpose.Contains('/') || purpose.Contains('\\'))
            throw new ArgumentException("Purpose must be a single directory name.", nameof(purpose));
        var directory = Path.Combine(GetParent(tempPath), purpose);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
