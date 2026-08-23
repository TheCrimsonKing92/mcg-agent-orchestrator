using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class SeedRepositoryRootInitializer
{
    private readonly object gate = new();
    private readonly Func<string> initialize;
    private string? initializedRoot;

    internal SeedRepositoryRootInitializer(Func<string> initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        this.initialize = initialize;
    }

    internal string EnsureInitialized()
    {
        lock (gate)
        {
            if (initializedRoot is not null)
            {
                return initializedRoot;
            }

            var root = initialize();
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("Seed repository root initializer returned no path.");
            }

            initializedRoot = root;
            return root;
        }
    }

    internal static string CreateProcessRoot(
        string processRootPath,
        Func<string, TempRootJanitorDeleteResult> deleteTree,
        Action<string> createDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processRootPath);
        ArgumentNullException.ThrowIfNull(deleteTree);
        ArgumentNullException.ThrowIfNull(createDirectory);

        var deleteResult = deleteTree(processRootPath);
        try
        {
            createDirectory(processRootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Seed repository root initialization failed for '{processRootPath}'. " +
                $"Delete status: {deleteResult.Status}; " +
                $"delete exception: {deleteResult.ExceptionType ?? "none"}; " +
                $"delete failure path: {deleteResult.FailurePath ?? "none"}; " +
                $"underlying exception: {exception.GetType().FullName}: {exception.Message}",
                exception);
        }

        return processRootPath;
    }
}
