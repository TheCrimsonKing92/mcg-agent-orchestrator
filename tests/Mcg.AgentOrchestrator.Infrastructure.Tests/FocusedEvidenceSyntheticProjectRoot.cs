using Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceSyntheticProjectRoot
{
    internal static Root Create(params string[] classNames)
    {
        var root = new Root(InfrastructureTestSupport.CreateTempDirectory());
        try
        {
            var relativeProject = AcceptancePolicyShardPlanner.InfrastructureTestsProject;
            var projectPath = Path.Combine(root.Path, relativeProject.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
            File.Copy(Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(),
                relativeProject.Replace('/', Path.DirectorySeparatorChar)), projectPath);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "SyntheticTests.cs"),
                string.Join(Environment.NewLine, classNames.Select(name => $"public sealed class {name} {{ }}")));
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    internal sealed class Root(string path) : IDisposable
    {
        internal string Path { get; } = path;

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Path);
    }
}
