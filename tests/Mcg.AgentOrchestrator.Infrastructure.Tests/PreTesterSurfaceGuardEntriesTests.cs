using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each worktree fixture owns a unique temporary directory.
public sealed class PreTesterSurfaceGuardEntriesTests
{
    [Fact]
    public void SurfaceGuardsFollowTheSixExistingEntriesWithReasons()
    {
        var entries = PreTesterAlwaysRunGuardTestClasses.Entries;

        Assert.Equal(9, entries.Count);
        Assert.Equal(
            new[] { "RuntimeAssemblyArchitectureTests", "ProcessStartInfoSourceGuardTests", "CanaryEngineSurfaceCoverageTests" },
            entries.Skip(6).Select(entry => entry.TestClass));
        Assert.All(entries.Skip(6), entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
    }

    [Fact]
    public void SurfaceFailureIdentitiesAreListedButLookalikeClassesAreNot()
    {
        Assert.True(PreTesterAlwaysRunGuardTestClasses.IsListed(
            "RuntimeAssemblyArchitectureTests.Process_Start_callers_are_sanctioned"));
        Assert.True(PreTesterAlwaysRunGuardTestClasses.IsListed(
            "ProcessStartInfoSourceGuardTests.TestProcessStartsOptOutOfVisibleConsoleWindows"));
        Assert.True(PreTesterAlwaysRunGuardTestClasses.IsListed(
            "CanaryEngineSurfaceCoverageTests.Collaborator_surface_exactly_matches_preexisting_coverage_gap"));
        Assert.False(PreTesterAlwaysRunGuardTestClasses.IsListed("RuntimeAssemblyArchitectureTestsExtra.Fact"));
    }

    [Fact]
    public void SelectFindsAllNineDeclaredGuardsInEntriesOrder()
    {
        string[] guardNames =
        [
            "GoalAcceptanceVerifierSplitFactParityTests",
            "TestProcessStopIdentityGuardTests",
            "CallerFilePathRootSourceGuardTests",
            "OrchestratorTempRootSourceGuardTests",
            "DirectGitLaunchSourceGuardTests",
            "ParallelSharedStateSourceGuardTests",
            "RuntimeAssemblyArchitectureTests",
            "ProcessStartInfoSourceGuardTests",
            "CanaryEngineSurfaceCoverageTests"
        ];
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
            var project = Path.Combine(root, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            foreach (var name in guardNames)
                File.WriteAllText(Path.Combine(project, name + ".cs"), $"public class {name} {{}}");

            var selections = PreTesterAlwaysRunGuardTestClasses.Select(root);

            Assert.Equal(9, selections.Count);
            Assert.Equal(PreTesterAlwaysRunGuardTestClasses.Entries.Select(entry => entry.TestClass),
                selections.Select(selection => selection.TestClass));
            Assert.All(selections, selection => Assert.Equal(projectName, selection.TestProject));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
