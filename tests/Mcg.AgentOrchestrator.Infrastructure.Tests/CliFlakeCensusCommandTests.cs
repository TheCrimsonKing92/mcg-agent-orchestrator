using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliFlakeCensusCommandTests
{
    [Fact(DisplayName = "flake_census_prints_ordered_filtered_rows_without_writing")]
    public void FlakeCensusPrintsOrderedFilteredRowsWithoutWriting()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var path = Path.Combine(
                workspace.OrchestratorDirectory,
                "acceptance-gate-attempts",
                AcceptanceFailingTestIndex.FileName);
            var now = DateTimeOffset.Parse("2026-09-23T12:00:00Z", null);
            var index = new AcceptanceFailingTestIndex(path);
            index.Append(
                [
                    Record("Example.Tests.Alpha.Flake", "goal-a", now.AddDays(-3)),
                    Record("Example.Tests.Alpha.Flake", "goal-b", now.AddDays(-2)) with { InsideChangedPaths = true },
                    Record("Example.Tests.Alpha.Flake", "goal-c", now.AddDays(-1)),
                    Record("Example.Tests.Beta.Flake", "goal-a", now.AddDays(-1)),
                    Record("Example.Tests.Beta.Flake", "goal-b", now)
                ],
                now);
            var before = File.ReadAllBytes(path);
            var filesBefore = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToArray();

            var output = Execute(workspace, ["flake-census"]);
            var filtered = Execute(workspace, ["flake-census", "--min-goals", "2", "--since", "2026-09-22"]);

            Assert.StartsWith("Example.Tests.Alpha.Flake | distinct-goals=3", output, StringComparison.Ordinal);
            Assert.Contains("outside-changed-paths=2 | total-failures=3 | first-seen=2026-09-20 | last-seen=2026-09-22", output, StringComparison.Ordinal);
            Assert.Contains("Example.Tests.Beta.Flake | distinct-goals=2", output, StringComparison.Ordinal);
            Assert.DoesNotContain("Example.Tests.Alpha.Flake", filtered, StringComparison.Ordinal);
            Assert.Contains("Example.Tests.Beta.Flake", filtered, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(
                filesBefore,
                Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .OrderBy(file => file, StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "flake_census_rejects_invalid_since_and_missing_index_is_empty")]
    public void FlakeCensusRejectsInvalidSinceAndMissingIndexIsEmpty()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);

            Assert.Equal(string.Empty, Execute(workspace, ["flake-census"]));
            var error = Assert.Throws<ArgumentException>(() =>
                Execute(workspace, ["flake-census", "--since", "yesterday"]));
            Assert.Contains("yyyy-MM-dd", error.Message, StringComparison.Ordinal);
            Assert.Contains("ISO 8601", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Execute(OrchestratorWorkspace workspace, string[] args)
    {
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return AsyncLocalConsoleRouter.Capture(() => Assert.False(CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal)));
    }

    private static AcceptanceFailingTestIndexRecord Record(
        string identity,
        string goalId,
        DateTimeOffset recordedAt) =>
        new(
            AcceptanceFailingTestIndex.ContractVersion,
            AcceptanceFailingTestIndexKinds.GateFailure,
            goalId,
            recordedAt,
            TestIdentity: identity,
            InsideChangedPaths: false);
}
