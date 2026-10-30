namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Rebases source paths, preserving opaque identities and every non-path row field.</summary>
internal static class JunitRecordPathRewriter
{
    internal static TestResult Rewrite(TestResult record, string repositoryRoot, string projectRoot)
    {
        if (record.File is not { Length: > 0 } file) return record;
        var normalized = file.Replace('\\', '/');
        if (!OperatingSystem.IsWindows() && (normalized.Contains(':') || normalized.StartsWith("//", StringComparison.Ordinal)))
            throw new IOException($"JUnit source path is outside the repository: {file}");
        var absolute = Path.GetFullPath(normalized, projectRoot);
        var roots = new[] { Path.GetFullPath(repositoryRoot), CrosswalkAcceptanceCheck.ResolveCrosswalkProjectRoot(repositoryRoot, ".") };
        var relative = roots.Select(root => Path.GetRelativePath(root, absolute).Replace('\\', '/'))
            .FirstOrDefault(path => JunitCommandCheckDefinition.IsRelativePath(path));
        if (relative is null) throw new IOException($"JUnit source path is outside the repository: {file}");
        var id = record.Id;
        if (id.StartsWith(file, StringComparison.Ordinal)) id = relative + id[file.Length..];
        else if (id.StartsWith(normalized, StringComparison.Ordinal)) id = relative + id[normalized.Length..];
        return record with { Id = id, File = relative };
    }
}
