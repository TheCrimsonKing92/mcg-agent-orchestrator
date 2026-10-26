// Parallel-safe: owned temporary roots and start-info dictionaries; no process environment mutation or child processes.
using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class NuGetPackagesRootAlignmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nuget-alignment", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LiveConductor_NoInheritedValue_BothPathsUseDefaultWithoutTrailingSeparator()
    {
        var worker = WorkerBuildRequestHost.BuildStartInfo(@"C:\repo\project", @"src\Fixture.csproj", "Debug", [],
            Path.GetTempPath(), readVariable: name => name == "NUGET_PACKAGES" ? null : Environment.GetEnvironmentVariable(name));
        var evidence = EvidenceStartInfo(null);
        var expected = AcceptanceTempRootNames.DefaultNuGetGlobalPackagesFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.Equal(worker.Environment["NUGET_PACKAGES"], evidence.Environment["NUGET_PACKAGES"], StringComparer.Ordinal);
        foreach (var start in new[] { worker, evidence })
        {
            var value = start.Environment["NUGET_PACKAGES"];
            Assert.Equal(expected, value, StringComparer.Ordinal);
            Assert.NotNull(value);
            Assert.False(value.EndsWith(Path.DirectorySeparatorChar));
            Assert.False(value.EndsWith(Path.AltDirectorySeparatorChar));
        }
    }

    [Theory]
    [InlineData(@"C:\repo\packages")]
    [InlineData(@"C:\repo\packages\")]
    public void InheritedValue_PassesThroughVerbatimOnBothPaths(string inheritedValue)
    {
        var worker = WorkerBuildRequestHost.BuildStartInfo(@"C:\repo\project", @"src\Fixture.csproj", "Debug", [],
            Path.GetTempPath(), readVariable: name => name == "NUGET_PACKAGES" ? inheritedValue : Environment.GetEnvironmentVariable(name));
        var evidence = EvidenceStartInfo(inheritedValue);

        Assert.Equal(inheritedValue, worker.Environment["NUGET_PACKAGES"], StringComparer.Ordinal);
        Assert.Equal(inheritedValue, evidence.Environment["NUGET_PACKAGES"], StringComparer.Ordinal);
        Assert.Equal(worker.Environment["NUGET_PACKAGES"], evidence.Environment["NUGET_PACKAGES"], StringComparer.Ordinal);
    }

    [Fact]
    public void Resolver_BlankInputs_ReturnSameDefault()
    {
        var expected = AcceptanceTempRootNames.DefaultNuGetGlobalPackagesFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var fromNull = AcceptanceTempRootNames.ResolveNuGetPackagesFolder(null);

        Assert.Equal(expected, fromNull, StringComparer.Ordinal);
        Assert.Equal(fromNull, AcceptanceTempRootNames.ResolveNuGetPackagesFolder(""), StringComparer.Ordinal);
        Assert.Equal(fromNull, AcceptanceTempRootNames.ResolveNuGetPackagesFolder("   "), StringComparer.Ordinal);
    }

    private ProcessStartInfo EvidenceStartInfo(string? inheritedValue)
    {
        var start = new ProcessStartInfo { CreateNoWindow = true };
        start.Environment.Remove("NUGET_PACKAGES");
        if (inheritedValue is not null) start.Environment["NUGET_PACKAGES"] = inheritedValue;
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(start.Environment,
            Path.Combine(_root, "repository"), Path.Combine(_root, "environment"));
        return start;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
