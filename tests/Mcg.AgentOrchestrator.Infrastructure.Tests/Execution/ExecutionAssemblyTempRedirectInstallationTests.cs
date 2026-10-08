using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Execution.Tests;

public sealed class ExecutionAssemblyTempRedirectInstallationTests
{
    [Fact]
    public void TempPathIsOwnedByCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tempPath = Normalize(Path.GetTempPath());
        Assert.True(
            AssemblyTempRedirect.TryParseProcessTempRootName(Path.GetFileName(tempPath), out var processId),
            $"Temp path '{tempPath}' is not an owned process root.");
        Assert.Equal(Environment.ProcessId, processId);

        var allowedParents = new List<string>
        {
            Normalize(Path.Combine(AppContext.BaseDirectory, ".test-tmp"))
        };
        foreach (var localAppData in new[]
                 {
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (!string.IsNullOrEmpty(localAppData))
                allowedParents.Add(Normalize(Path.Combine(localAppData, "Temp", "Low", "mcg-tests")));
        }

        var parent = Path.GetDirectoryName(tempPath);
        Assert.True(
            allowedParents.Any(candidate => string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)),
            $"Temp path '{tempPath}' has parent '{parent}'; expected one of: {string.Join(", ", allowedParents)}.");
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

