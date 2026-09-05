using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

public sealed class AcceptanceLaneClosureHasherTests : GoalAcceptanceVerifierTestBase, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-closure-hasher-{Guid.NewGuid():N}");

    [Fact]
    public void DirtyTrackedClosureMember_RefusesHash()
    {
        CreateRepository("src/TestLibrary/TestLibrary.csproj");
        var check = CreateCheck();

        Assert.NotNull(AcceptanceLaneClosureHasher.TryCompute(_root, check));

        File.AppendAllText(Path.Combine(_root, "src", "TestLibrary", "Probe.cs"), "// dirty");

        Assert.Null(AcceptanceLaneClosureHasher.TryCompute(_root, check));
    }

    [Fact]
    public void MissingReferencedProject_RefusesHash()
    {
        CreateRepository("src/Missing/Missing.csproj", createReferencedProject: false);

        Assert.Null(AcceptanceLaneClosureHasher.TryCompute(_root, CreateCheck()));
    }

    [Fact]
    public void GitCannotResolveCleanClosureMember_RefusesHash()
    {
        CreateRepository("src/TestLibrary/TestLibrary.csproj", trackRootBuildInput: false);

        Assert.Null(AcceptanceLaneClosureHasher.TryCompute(_root, CreateCheck()));
    }

    public void Dispose() => DeleteDirectoryWithRetry(_root);

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck CreateCheck() => new()
    {
        Name = "infrastructure tests: Closure refusal",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~AcceptanceLaneClosureHasherTests"]
    };

    private void CreateRepository(
        string referencedProject,
        bool createReferencedProject = true,
        bool trackRootBuildInput = true)
    {
        var testProject = Path.Combine(_root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(testProject);
        File.WriteAllText(
            Path.Combine(testProject, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../{referencedProject}\" /></ItemGroup></Project>");
        if (createReferencedProject)
        {
            var referencedProjectPath = Path.Combine(_root, referencedProject.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(referencedProjectPath)!);
            File.WriteAllText(referencedProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(referencedProjectPath)!, "Probe.cs"), "internal sealed class Probe { }");
        }
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        if (!trackRootBuildInput)
            File.WriteAllText(Path.Combine(_root, ".gitignore"), "/Directory.Build.props");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial closure");
    }

    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
