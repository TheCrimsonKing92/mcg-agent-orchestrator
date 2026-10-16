using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceOwnerResultsTestRoot : IDisposable
{
    private AcceptanceOwnerResultsTestRoot(string root)
    {
        Root = root;
        ResultsPrefix = Path.Combine(root, "gate");
    }

    public string Root { get; }

    public string ResultsPrefix { get; }

    public static AcceptanceOwnerResultsTestRoot Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-owner-results-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new AcceptanceOwnerResultsTestRoot(root);
    }

    public AcceptanceRunExecutionOptions CreateOptions(Action<AcceptanceGateProgress>? progressSink) =>
        new(ProgressSink: progressSink, ResultsPrefix: ResultsPrefix);

    public void Dispose()
    {
        // Match GoalAcceptanceVerifierTestBase.DeleteDirectoryWithRetry's cleanup policy.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                System.Threading.Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                ClearReadOnlyAttributes(Root);
                System.Threading.Thread.Sleep(100);
            }
        }

        Directory.Delete(Root, recursive: true);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            var root = new DirectoryInfo(path);
            root.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next delete attempt surfaces any remaining failure.
        }
    }
}
