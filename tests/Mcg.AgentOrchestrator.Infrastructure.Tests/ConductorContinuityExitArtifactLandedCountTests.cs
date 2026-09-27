using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorContinuityExitArtifactLandedCountTests
{
    [Xunit.Fact]
    public void OldArtifactDefaultsLandedGoalsToZero_AndNewArtifactRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-exit-artifact-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path,
                "{\"StopReason\":\"max-duration\",\"Ticks\":1,\"Done\":0,\"RestartRequested\":true}");
            Assert.Equal(0, ConductorContinuityExitArtifact.TryRead(path)?.LandedGoals);

            ConductorContinuityExitArtifact.Write(path,
                new ConductorContinuityExitArtifact("self-relaunch-handoff", 2, 0,
                    RestartRequested: true, LandedGoals: 2));
            Assert.Contains("\"landedGoals\":2", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal(2, ConductorContinuityExitArtifact.TryRead(path)?.LandedGoals);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
