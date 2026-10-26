// Parallel-safe: owned temporary repositories, artifacts and child environments; no global process operations.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SourceLinkIncrementalBuildTests(ITestOutputHelper output) : IDisposable
{
    private const string CompileSkipped = "Skipping target \"CoreCompile\" because all output files are up-to-date";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sourcelink-build", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RepositoryProps_SourceLinkAndRevision_AreDisabledUnconditionally()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        var group = props.Root!.Elements().First(element =>
            element.Name.LocalName == "PropertyGroup" && element.Attribute("Condition") is null);
        foreach (var name in new[] { "EnableSourceLink", "IncludeSourceRevisionInInformationalVersion" })
        {
            var property = Assert.Single(group.Elements().Where(element => element.Name.LocalName == name));
            Assert.Equal("false", property.Value.Trim());
        }
    }

    [Fact]
    public void UnrelatedCommit_SkipsCompile_SourceChangeRebuildsAssembly()
    {
        var (tree, artifacts) = Seed("source-change");
        Build(tree, artifacts);
        var assembly = IntermediateAssembly(artifacts);
        var writtenAtA = File.GetLastWriteTimeUtc(assembly);
        var hashAtA = Hash(assembly);

        CommitUnrelatedFile(tree);
        var buildB = Build(tree, artifacts);
        Assert.Contains(CompileSkipped, buildB, StringComparison.Ordinal);
        Assert.Equal(writtenAtA, File.GetLastWriteTimeUtc(assembly));
        Assert.Equal(hashAtA, Hash(assembly));
        Assert.Empty(Directory.EnumerateFiles(artifacts, "Fixture.sourcelink.json", SearchOption.AllDirectories));
        var hashAtB = Hash(assembly);

        File.WriteAllText(Path.Combine(tree, "Fixture", "Value.cs"),
            "public static class Value { public static int Read() => 43; }");
        Commit(tree, "C: change source constant");
        var buildC = Build(tree, artifacts);
        Assert.DoesNotContain(CompileSkipped, buildC, StringComparison.Ordinal);
        Assert.NotEqual(hashAtB, Hash(assembly));
    }

    [Fact]
    public void SourceLinkEnabled_UnrelatedCommit_StampsHeadAndRecompiles()
    {
        var (tree, artifacts) = Seed("mechanism");
        Build(tree, artifacts, enableSourceLink: true);
        CommitUnrelatedFile(tree);
        var headB = Run(tree, "git", "rev-parse", "HEAD").Trim();
        Assert.Matches("^[0-9a-f]{40}$", headB);
        var buildB = Build(tree, artifacts, enableSourceLink: true);

        var sourceLink = Assert.Single(Directory.EnumerateFiles(
            artifacts, "Fixture.sourcelink.json", SearchOption.AllDirectories));
        Assert.Contains(headB, File.ReadAllText(sourceLink), StringComparison.Ordinal);
        Assert.DoesNotContain(CompileSkipped, buildB, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedHead_SecondBuild_SkipsCompile()
    {
        var (tree, artifacts) = Seed("unchanged-head");
        Build(tree, artifacts);
        var secondBuild = Build(tree, artifacts);
        Assert.Contains(CompileSkipped, secondBuild, StringComparison.Ordinal);
    }

    private (string Tree, string Artifacts) Seed(string name)
    {
        var owned = Path.Combine(_root, name);
        var tree = Path.Combine(owned, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "Fixture"));
        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "global.json" })
            File.Copy(Path.Combine(RepositoryRoot(), file), Path.Combine(tree, file));
        File.WriteAllText(Path.Combine(tree, "Fixture", "Fixture.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NuGetAudit>false</NuGetAudit>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(tree, "Fixture", "Value.cs"),
            "public static class Value { public static int Read() => 42; }");
        Run(tree, "git", "init");
        Run(tree, "git", "remote", "add", "origin", "https://github.com/example/fixture-repo");
        Commit(tree, "A: fixture");
        return (tree, Path.Combine(owned, "artifacts"));
    }

    private void CommitUnrelatedFile(string tree)
    {
        File.WriteAllText(Path.Combine(tree, "unrelated.txt"), "Commit B changes no fixture inputs.");
        Commit(tree, "B: unrelated text");
    }

    private void Commit(string tree, string message)
    {
        Run(tree, "git", "add", "--all");
        Run(tree, "git", "commit", "-m", message);
    }

    private string Build(string tree, string artifacts, bool enableSourceLink = false)
    {
        var arguments = new List<string>
        {
            "build", "Fixture/Fixture.csproj", "-v:n", "--artifacts-path", artifacts,
            "-p:UseSharedCompilation=false", "-nodeReuse:false"
        };
        if (enableSourceLink) arguments.Add("-p:EnableSourceLink=true");
        return Run(tree, "dotnet", arguments.ToArray());
    }

    private string Run(string tree, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = tree, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var environment = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        var environmentRoot = Path.Combine(Path.GetDirectoryName(tree)!, "environment");
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(environment, tree, environmentRoot);
        environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        // Keep fixture-created profiles, git configuration and caches inside this test's owned root.
        var profile = Path.Combine(environmentRoot, "profile");
        Directory.CreateDirectory(profile);
        var gitConfig = Path.Combine(profile, "gitconfig");
        if (!File.Exists(gitConfig)) File.Copy(environment["GIT_CONFIG_GLOBAL"]!, gitConfig);
        environment["GIT_CONFIG_GLOBAL"] = gitConfig;
        environment["HOME"] = profile;
        environment["USERPROFILE"] = profile;
        environment["NUGET_PACKAGES"] = Path.Combine(environmentRoot, "packages");
        environment["TEMP"] = environment["TMP"] = Path.Combine(environmentRoot, "temp");
        Directory.CreateDirectory(environment["TEMP"]!);
        start.Environment.Clear();
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        var result = TestChildProcessCapture.Run(start, hangBound: TimeSpan.FromMinutes(3));
        output.WriteLine($"{executable} {string.Join(' ', arguments)}\n{result.Stdout}\n{result.Stderr}");
        Assert.True(result.ExitCode == 0,
            $"{executable} exited {result.ExitCode}.\n{result.Stdout}\n{result.Stderr}");
        return result.Stdout;
    }

    private static string IntermediateAssembly(string artifacts)
    {
        // The implementation assembly is directly in obj/Fixture/debug, never bin, ref or refint.
        var path = Path.Combine(artifacts, "obj", "Fixture", "debug", "Fixture.dll");
        Assert.True(File.Exists(path), $"Missing intermediate assembly: {path}");
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        VerifiedRepositoryRoot.Resolve(source, Environment.GetEnvironmentVariable);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // Git marks its owned object files read-only on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
