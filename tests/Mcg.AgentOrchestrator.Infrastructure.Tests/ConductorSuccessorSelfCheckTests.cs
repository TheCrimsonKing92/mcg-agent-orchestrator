using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ConductorSuccessorSelfCheckTests
{
    [Xunit.Fact(DisplayName = "ConductorSuccessorSelfCheck_rejects_incomplete_protocol_arguments")]
    public void RejectsIncompleteProtocolArguments()
    {
        var result = ConductorSuccessorSelfCheck.Run(
            [ConductorSuccessorSelfCheck.SubcommandName, "only-one-argument"]);

        Assert.Equal(2, result);
    }

    [Xunit.Fact(DisplayName = "ConductorSuccessorSelfCheck_corrupt_state_is_classified_as_self_check_failure")]
    public void CorruptStateIsClassifiedAsSelfCheckFailure()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var stateStorePath = Path.Combine(root, "state.db");
        var markerPath = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll.git-head");
        var previousMarker = File.Exists(markerPath) ? File.ReadAllBytes(markerPath) : null;
        try
        {
            File.WriteAllText(stateStorePath, "not-a-sqlite-database");
            File.WriteAllText(markerPath, "test-head");
            var exitCode = -1;
            var stderr = CaptureConsoleError(() => exitCode = ConductorSuccessorSelfCheck.Run(
                [
                    ConductorSuccessorSelfCheck.SubcommandName,
                    InfrastructureTestSupport.FindRepositoryRoot(),
                    stateStorePath,
                    "test-head",
                    Path.Combine(root, "agents.json"),
                    Path.Combine(root, "worker-profiles.json"),
                    Path.Combine(root, "model-functions.json")
                ]));

            Assert.Equal(1, exitCode);
            Assert.Contains("LOOP_SELF_CHECK_FAILED", stderr, StringComparison.Ordinal);
            Assert.Contains("SQLite", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (previousMarker is null)
            {
                File.Delete(markerPath);
            }
            else
            {
                File.WriteAllBytes(markerPath, previousMarker);
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
