// Parallel-safe: each fact owns its fixture, artifacts and child environments; the shared packages folder is never cleaned.
using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class NuGetRestoreNoOpAcrossBuildPathsTests(ITestOutputHelper output) : IDisposable
{
    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <NuGetAudit>false</NuGetAudit>
          </PropertyGroup>
        </Project>
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nuget-build-paths", Guid.NewGuid().ToString("N"));
    private string Artifacts => Path.Combine(_root, "artifacts");
    private string FixtureProject => Path.Combine(_root, "Fixture", "Fixture.csproj");

    [Fact]
    public void WorkerThenEvidenceBuild_SameArtifacts_SkipsRestoreAndCompile()
    {
        Seed();
        var worker = WorkerStartInfo();
        Run(worker);
        var restoreFiles = new[] { "project.assets.json", "Fixture.csproj.nuget.g.props", "project.nuget.cache" }
            .Select(RestoreFile)
            .ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), WrittenAt: File.GetLastWriteTimeUtc(path)));
        var assembly = IntermediateAssembly();
        var assemblyWrittenAt = File.GetLastWriteTimeUtc(assembly);

        var buildB = Run(EvidenceStartInfo(worker));

        Assert.DoesNotContain(Lines(buildB), line => line.Contains("Restored ", StringComparison.Ordinal));
        foreach (var (path, snapshot) in restoreFiles)
        {
            Assert.Equal(snapshot.Bytes, File.ReadAllBytes(path));
            Assert.Equal(snapshot.WrittenAt, File.GetLastWriteTimeUtc(path));
        }
        Assert.Equal(assemblyWrittenAt, File.GetLastWriteTimeUtc(assembly));
    }

    [Fact]
    public void EvidenceBuild_TrailingSeparatorFlip_Restores()
    {
        Seed();
        var worker = WorkerStartInfo();
        Run(worker);
        var props = RestoreFile("Fixture.csproj.nuget.g.props");
        var original = File.ReadAllBytes(props);

        var buildB = Run(EvidenceStartInfo(worker, worker.Environment["NUGET_PACKAGES"] + Path.DirectorySeparatorChar));

        Assert.Contains(Lines(buildB), line => line.Contains("Restored ", StringComparison.Ordinal));
        Assert.False(original.SequenceEqual(File.ReadAllBytes(props)), "The separator flip must change NuGet's generated props.");
    }

    [Fact]
    public void ProjectReferenceAdded_AfterNoOpPair_Restores()
    {
        Seed();
        var worker = WorkerStartInfo();
        Run(worker);
        var buildB = Run(EvidenceStartInfo(worker));
        Assert.DoesNotContain(Lines(buildB), line => line.Contains("Restored ", StringComparison.Ordinal));

        Directory.CreateDirectory(Path.Combine(_root, "Lib"));
        File.WriteAllText(Path.Combine(_root, "Lib", "Lib.csproj"), ProjectXml);
        File.WriteAllText(Path.Combine(_root, "Lib", "LibValue.cs"),
            "public static class LibValue { public static int Read() => 7; }");
        File.WriteAllText(FixtureProject, ProjectXml.Replace("</Project>", """
            <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
            </Project>
            """, StringComparison.Ordinal));

        var changedGraph = Run(WorkerStartInfo());

        Assert.Contains(Lines(changedGraph), line => line.Contains("Restored ", StringComparison.Ordinal));
        Assert.Contains("Lib", File.ReadAllText(RestoreFile("project.assets.json")), StringComparison.Ordinal);
    }

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Fixture"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(FixtureProject, ProjectXml);
        File.WriteAllText(Path.Combine(_root, "Fixture", "Value.cs"),
            "public static class Value { public static int Read() => 42; }");
    }

    private ProcessStartInfo WorkerStartInfo()
    {
        var temporaryDirectory = Path.Combine(_root, "temp");
        Directory.CreateDirectory(temporaryDirectory);
        var start = WorkerBuildRequestHost.BuildStartInfo(_root, @"Fixture\Fixture.csproj", "Debug",
            ["--artifacts-path", Artifacts], temporaryDirectory,
            readVariable: name => name == "NUGET_PACKAGES" ? null : Environment.GetEnvironmentVariable(name));
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return start;
    }

    private ProcessStartInfo EvidenceStartInfo(ProcessStartInfo worker, string? inheritedValue = null)
    {
        var start = new ProcessStartInfo(worker.FileName)
        {
            WorkingDirectory = worker.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in worker.ArgumentList) start.ArgumentList.Add(argument);
        start.Environment.Remove("NUGET_PACKAGES");
        if (inheritedValue is not null) start.Environment["NUGET_PACKAGES"] = inheritedValue;
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(start.Environment, _root, Path.Combine(_root, "environment"));
        return start;
    }

    private string Run(ProcessStartInfo start)
    {
        var result = TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(3));
        output.WriteLine($"{start.FileName} {string.Join(' ', start.ArgumentList)}\n{result.Stdout}\n{result.Stderr}");
        Assert.True(result.ExitCode == 0, $"Fixture build exited {result.ExitCode}.\n{result.Stdout}\n{result.Stderr}");
        return result.Stdout;
    }

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private string RestoreFile(string name)
    {
        var path = Path.Combine(Artifacts, "obj", "Fixture", name);
        Assert.True(File.Exists(path), $"Missing restore output: {path}");
        return path;
    }

    private string IntermediateAssembly()
    {
        var path = Path.Combine(Artifacts, "obj", "Fixture", "debug", "Fixture.dll");
        Assert.True(File.Exists(path), $"Missing intermediate assembly: {path}");
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
