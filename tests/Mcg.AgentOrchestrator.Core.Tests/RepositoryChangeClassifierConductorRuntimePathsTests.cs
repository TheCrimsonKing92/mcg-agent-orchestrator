using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

public sealed class RepositoryChangeClassifierConductorRuntimePathsTests
{
    [Xunit.Theory]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.GoalLifecycle.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Domain/RetryAdmission.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.OwnedRoots.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/StorageRetentionMaintenance.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure/Persistence/OperatorIntentStore.cs")]
    [Xunit.InlineData("src\\Mcg.AgentOrchestrator.Core\\Domain\\RetryAdmission.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/appsettings.json")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/Resources/Messages.resx")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Providers/Provider.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/Comms.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/Mcg.AgentOrchestrator.App.csproj")]
    [Xunit.InlineData("config/acceptance-manifest.json")]
    [Xunit.InlineData("Directory.Build.props")]
    [Xunit.InlineData("Directory.Build.targets")]
    [Xunit.InlineData("Directory.Build.rsp")]
    [Xunit.InlineData("Directory.Packages.props")]
    [Xunit.InlineData("global.json")]
    [Xunit.InlineData("NuGet.Config")]
    [Xunit.InlineData("scripts/resolve-run-dir.ps1")]
    [Xunit.InlineData("scripts/Update-AppDllGitHeadMarker.ps1")]
    [Xunit.InlineData("mcg-orchestrator.cmd")]
    public void ConductorRuntimePathsRequireRelaunch(string path)
    {
        Assert.True(RepositoryChangeClassifier.Classify([path]).RequiresConductorRelaunch, path);
        Assert.True(RepositoryChangeClassifier.DecideConductorRelaunch([path]).Required, path);
    }

    [Xunit.Theory]
    [Xunit.InlineData("docs/operator-runbook.md")]
    [Xunit.InlineData("README.md")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/README.md")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/notes.txt")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryChangeClassifierTests.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Dashboard/Program.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Dashboard/Mcg.AgentOrchestrator.Dashboard.csproj")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/bin/Debug/x.dll")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/obj/Debug/x.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/.scratch/x.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/.orchestrator-prototype/x.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/.orchestrator-worktrees/x.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Core/TestResults/x.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.App/playwright-report/x.cs")]
    [Xunit.InlineData("src/Mcg.AgentOrchestrator.Infrastructure.Unreferenced/Store.cs")]
    [Xunit.InlineData("scripts/other.ps1")]
    [Xunit.InlineData(".orchestrator/state.json")]
    public void NonConductorPathsDoNotRequireRelaunch(string path)
    {
        Assert.False(RepositoryChangeClassifier.Classify([path]).RequiresConductorRelaunch, path);
        Assert.False(RepositoryChangeClassifier.DecideConductorRelaunch([path]).Required, path);
    }

    [Xunit.Fact]
    public void EveryNamedExclusionStaysOutsideTheRelaunchSet()
    {
        Assert.Equal(["src/Mcg.AgentOrchestrator.App/Dashboard/"],
            RepositoryChangeClassifier.ConductorExcludedPathPrefixes);
        foreach (var prefix in RepositoryChangeClassifier.ConductorExcludedPathPrefixes)
        {
            Assert.False(RepositoryChangeClassifier.Classify([prefix]).RequiresConductorRelaunch);
            foreach (var suffix in new[] { "Api/X.cs", "Hosting/X.cs", "Rendering/X.cs" })
            {
                Assert.False(RepositoryChangeClassifier.Classify([prefix + suffix]).RequiresConductorRelaunch);
            }
        }
    }

    [Xunit.Fact]
    public void MixedChangesReportTheRequiredConductorSourceClassification()
    {
        var paths = new[]
        {
            "docs/operator-runbook.md",
            "src/Mcg.AgentOrchestrator.Core/Domain/RetryAdmission.cs"
        };
        Assert.Equal("conductor-source", RepositoryChangeClassifier.DecideConductorRelaunch(paths).Classification);
        Assert.Equal("no-changed-files", RepositoryChangeClassifier.DecideConductorRelaunch([]).Classification);
        Assert.Equal("outside-conductor-sources", RepositoryChangeClassifier.DecideConductorRelaunch(
            ["scripts/other.ps1"]).Classification);
    }

    [Xunit.Fact]
    public void ConductorRootsMatchAppProjectReferenceClosure()
    {
        var root = FindRepositoryRoot();
        var appProject = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(appProject);
        while (pending.TryPop(out var project))
        {
            project = Path.GetFullPath(project);
            if (!visited.Add(project))
            {
                continue;
            }

            foreach (var reference in XDocument.Load(project).Descendants()
                         .Where(element => element.Name.LocalName == "ProjectReference")
                         .Select(element => (string?)element.Attribute("Include")))
            {
                Assert.False(string.IsNullOrWhiteSpace(reference), project);
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, reference!)));
            }
        }

        var actual = visited.Select(project =>
                Path.GetRelativePath(root, Path.GetDirectoryName(project)!).Replace('\\', '/') + "/")
            .Order(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RepositoryChangeClassifier.ConductorSourceRoots.Order(StringComparer.OrdinalIgnoreCase), actual);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(sourceFilePath);
    }
}
