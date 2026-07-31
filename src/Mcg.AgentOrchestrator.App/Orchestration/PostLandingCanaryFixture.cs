namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PostLandingCanaryFixture : IDisposable
{
    private readonly string _root;
    private bool _disposed;

    private PostLandingCanaryFixture(string root)
    {
        _root = root;
    }

    internal string RootPath => _root;

    internal static PostLandingCanaryFixture Materialize(string repositoryRoot, string landingSha)
    {
        var source = Path.Combine(repositoryRoot, "tests", "canary-fixture");
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Post-landing canary fixture source was not found: {source}");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "mcg-post-landing-canary",
            landingSha[..Math.Min(12, landingSha.Length)],
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, sourcePath);
                if (relative.EndsWith(".template", StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative[..^".template".Length];
                }

                var destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourcePath, destination, overwrite: false);
            }

            return new PostLandingCanaryFixture(root);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDelete(_root);
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    ClearReadOnlyAttributes(path);
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (
                attempt < 5 &&
                ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            var root = new DirectoryInfo(path);
            if ((root.Attributes & FileAttributes.ReadOnly) != 0)
            {
                root.Attributes &= ~FileAttributes.ReadOnly;
            }

            foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the bounded delete retry surfaces any persistent failure.
        }
    }
}
