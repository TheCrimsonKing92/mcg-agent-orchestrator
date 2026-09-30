using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: reflection and read-only source inspection; no processes or shared writes.
public sealed class ConductorTickPushRetirementTests
{
    [Fact]
    public void TickPusherDeclaresOnlyStoreCallbackAndRecordingSurface()
    {
        var type = typeof(ConductorTickPusher);
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var retiredMember in new[] { "TryPush", "CreateCallback", "TryReadDashboardUrl" })
            Assert.DoesNotContain(type.GetMembers(declared), member => member.Name == retiredMember);
        Assert.DoesNotContain(type.GetFields(declared), field =>
            field.IsStatic && field.FieldType == typeof(HttpClient));

        var callback = type.GetMethod("CreateStoreCallback", BindingFlags.Public | BindingFlags.Static |
            BindingFlags.DeclaredOnly, null, [typeof(string)], null);
        Assert.NotNull(callback);
        Assert.Equal(typeof(Action<BatchTickSummary>), callback.ReturnType);

        var record = type.GetMethod("TryRecord", BindingFlags.Public | BindingFlags.Static |
            BindingFlags.DeclaredOnly, null, [typeof(string), typeof(BatchTickSummary)], null);
        Assert.NotNull(record);
        Assert.Equal(typeof(void), record.ReturnType);
    }

    [Fact]
    public void ConductLoopUsesOnlyStoreCallback()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "Mcg.AgentOrchestrator.App",
            "Cli", "CliCommandHandlers.Goals.cs");
        Assert.True(File.Exists(path), $"Missing conduct source: {path}");
        var source = File.ReadAllText(path);

        foreach (var retiredToken in new[]
            { "--dashboard-url", "TryReadDashboardUrl", "CreateCallback(", "Dashboard compatibility push" })
            Assert.DoesNotContain(retiredToken, source, StringComparison.Ordinal);
        Assert.Contains(
            "Action<BatchTickSummary>? onTick = ConductorTickPusher.CreateStoreCallback(context.Workspace.RunEventStorePath);",
            source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
