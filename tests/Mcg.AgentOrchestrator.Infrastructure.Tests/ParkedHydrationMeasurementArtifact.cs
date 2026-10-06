using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;

internal static class ParkedHydrationMeasurementArtifact
{
    internal static string Write(
        string ownedRoot,
        int parkedGoalCount,
        long preFixGoalJsonBytes,
        long afterGoalJsonBytes,
        double reduction,
        long workingSetBefore,
        long workingSetAfter)
    {
        Directory.CreateDirectory(ownedRoot);
        var artifactPath = Path.Combine(ownedRoot, "parked-hydration-measurement.json");
        File.WriteAllText(
            artifactPath,
            JsonSerializer.Serialize(
                new
                {
                    fixture = "conduct-loop-parked-hydration",
                    parked_goal_count = parkedGoalCount,
                    safety_net_sweep_cadence_ticks = CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks,
                    pre_fix_goal_json_bytes = preFixGoalJsonBytes,
                    after_goal_json_bytes = afterGoalJsonBytes,
                    reduction,
                    working_set_before = workingSetBefore,
                    working_set_after = workingSetAfter
                },
                new JsonSerializerOptions { WriteIndented = true }));

        return artifactPath;
    }
}
