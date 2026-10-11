using System.Text;

internal sealed class SourceSnapshotFixture : IDisposable
{
    public string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
    public string TestProject => Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
    public string CoreProject => Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Core.Tests");

    public SourceSnapshotFixture()
    {
        Directory.CreateDirectory(TestProject);
        Directory.CreateDirectory(CoreProject);
        Directory.CreateDirectory(Path.Combine(Root, "src", "Mcg.AgentOrchestrator.Infrastructure"));
        Directory.CreateDirectory(Path.Combine(Root, "src", "Mcg.AgentOrchestrator.Execution", "Processes"));
        Directory.CreateDirectory(Path.Combine(Root, "src", "Mcg.AgentOrchestrator.Execution", "Workers"));
    }

    public string Write(string relativePath, string text, bool bom = false)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(bom));
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
