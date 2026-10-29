using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class CliBacklogUpdateWorkspaceTemplate
{
    private static readonly Lazy<string> Template = new(
        CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void CopyTo(string destinationDirectory)
    {
        var source = Template.Value;
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetRelativePath(source, file)));
        }
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-cli-backlog-update-tests", "template-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            // backlog-update reads only this store. Setup closes its unpooled connection before copying.
            BacklogStore.Setup(OrchestratorWorkspace.ForDirectory(root).BacklogStorePath);
            return root;
        }
        catch (Exception exception)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw new InvalidOperationException("CLI backlog-update workspace template could not be created.", exception);
        }
    }
}
