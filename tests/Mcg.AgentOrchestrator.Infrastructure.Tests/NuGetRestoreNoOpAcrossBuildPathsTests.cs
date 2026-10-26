// Parallel-safe: each fact owns its fixture, artifacts and child environments; the shared packages folder is never cleaned.
using System.Diagnostics;
using System.Xml.Linq;
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

    [Fact]
    public void XunitExeBuiltUnderAnotherProfile_KeepsEntryPoint()
    {
        var sharedPackages = WorkerStartInfo().Environment["NUGET_PACKAGES"]!;
        SeedXunitProject(sharedPackages);
        // Leave room for long transitive package filenames under the isolated profile on Windows.
        var workerProfile = Path.Combine(_root, "w");
        var evidenceProfile = Path.Combine(_root, "evidence-profile");
        Directory.CreateDirectory(evidenceProfile);
        var worker = IsolatedWorkerStartInfo(workerProfile);
        Run(worker);

        var propsPath = RestoreFile("Fixture.csproj.nuget.g.props");
        var originalProps = File.ReadAllBytes(propsPath);
        var packageRoot = XDocument.Load(propsPath).Descendants()
            .Single(element => element.Name.LocalName == "NuGetPackageRoot").Value;
        Assert.StartsWith("$(UserProfile)", packageRoot, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(evidenceProfile, ".nuget")));

        var sourcePath = Path.Combine(_root, "Fixture", "Value.cs");
        File.WriteAllText(sourcePath, "public class Example { [Xunit.Fact] public void SecondBuild() { } }");
        // Make compilation necessary even when the filesystem gives both writes the same timestamp.
        File.SetLastWriteTimeUtc(sourcePath, File.GetLastWriteTimeUtc(IntermediateAssembly()).AddSeconds(1));
        var evidence = EvidenceStartInfo(worker, worker.Environment["NUGET_PACKAGES"]);
        evidence.Environment.Clear();
        foreach (var (name, value) in worker.Environment) evidence.Environment[name] = value;
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(
            evidence.Environment, _root, Path.Combine(_root, "environment"));
        evidence.Environment["USERPROFILE"] = evidenceProfile;
        evidence.Environment["HOME"] = evidenceProfile;
        var buildB = TestChildProcessCapture.Run(evidence, hangBound: TimeSpan.FromMinutes(3));
        output.WriteLine($"{buildB.Stdout}\n{buildB.Stderr}");

        Assert.True(buildB.ExitCode == 0, $"Second build exited {buildB.ExitCode}.\n{buildB.Stdout}\n{buildB.Stderr}");
        Assert.DoesNotContain("CS5001", buildB.Stdout + buildB.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Lines(buildB.Stdout), line => line.Contains("Restored ", StringComparison.Ordinal));
        Assert.Equal(originalProps, File.ReadAllBytes(propsPath));

        evidence.ArgumentList.Clear();
        evidence.ArgumentList.Add("exec");
        evidence.ArgumentList.Add(Path.Combine(Artifacts, "bin", "Fixture", "debug", "Fixture.dll"));
        evidence.ArgumentList.Add("--list-tests");
        var discovery = TestChildProcessCapture.Run(evidence, hangBound: TimeSpan.FromMinutes(3));
        output.WriteLine($"{discovery.Stdout}\n{discovery.Stderr}");
        Assert.True(discovery.ExitCode == 0, $"Entry point exited {discovery.ExitCode}.\n{discovery.Stdout}\n{discovery.Stderr}");
        Assert.Contains("SecondBuild", discovery.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstBuild", discovery.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitNuGetPackageRoot_IsNotOverridden()
    {
        Seed();
        var explicitRoot = Path.Combine(_root, "explicit-packages") + Path.DirectorySeparatorChar;
        WriteRepositoryPropsImport(explicitRoot);
        var evaluation = IsolatedWorkerStartInfo(Path.Combine(_root, "evaluation-profile"));
        evaluation.Environment["NUGET_PACKAGES"] = Path.Combine(_root, "other-packages");
        evaluation.ArgumentList.Clear();
        foreach (var argument in new[] { "msbuild", FixtureProject, "--nologo", "-getProperty:NuGetPackageRoot" })
            evaluation.ArgumentList.Add(argument);

        Assert.Equal(explicitRoot, Run(evaluation).Trim());
    }

    private ProcessStartInfo IsolatedWorkerStartInfo(string profile)
    {
        Directory.CreateDirectory(profile);
        var start = WorkerStartInfo();
        start.Environment["USERPROFILE"] = profile;
        start.Environment["HOME"] = profile;
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(profile, "cli-home");
        start.Environment["NUGET_PACKAGES"] = Path.Combine(profile, ".nuget", "packages");
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        return start;
    }

    private void SeedXunitProject(string sharedPackages)
    {
        Seed();
        WriteRepositoryPropsImport();
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "shared-packages"), new XAttribute("value", sharedPackages)))))
            .Save(Path.Combine(_root, "NuGet.Config"));
        var repositoryProject = XDocument.Load(Path.Combine(VerifiedRepositoryRoot.Find(), "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"));
        var version = repositoryProject.Descendants("PackageReference")
            .Single(element => (string?)element.Attribute("Include") == "xunit.v3.mtp-v2")
            .Attribute("Version")!.Value;
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("OutputType", "Exe"),
                new XElement("IsTestProject", "true"),
                new XElement("UseMicrosoftTestingPlatformRunner", "true"),
                new XElement("NuGetAudit", "false")),
            new XElement("ItemGroup", new XElement("PackageReference",
                new XAttribute("Include", "xunit.v3.mtp-v2"), new XAttribute("Version", version)))))
            .Save(FixtureProject);
        File.WriteAllText(Path.Combine(_root, "Fixture", "Value.cs"),
            "public class Example { [Xunit.Fact] public void FirstBuild() { } }");
    }

    private void WriteRepositoryPropsImport(string? explicitPackageRoot = null)
    {
        var project = new XElement("Project");
        if (explicitPackageRoot is not null)
            project.Add(new XElement("PropertyGroup", new XElement("NuGetPackageRoot", explicitPackageRoot)));
        project.Add(new XElement("Import",
            new XAttribute("Project", Path.Combine(VerifiedRepositoryRoot.Find(), "Directory.Build.props"))));
        new XDocument(project).Save(Path.Combine(_root, "Directory.Build.props"));
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
