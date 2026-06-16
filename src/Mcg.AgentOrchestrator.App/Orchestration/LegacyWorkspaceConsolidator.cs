using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class LegacyWorkspaceConsolidator
{
    // Path from repo root to the pre-root-anchor legacy state directory.
    internal const string LegacyRelativePath = "src/Mcg.AgentOrchestrator.App/.orchestrator";

    internal sealed record ConsolidationResult(
        bool IsNoOp,
        string? NoOpReason,
        IReadOnlyList<string> MergedGoalIds,
        IReadOnlyList<string> SkippedGoalIds,
        string? ArchivePath)
    {
        public static ConsolidationResult NoOp(string reason) =>
            new(true, reason, [], [], null);
    }

    public static async Task<ConsolidationResult> ConsolidateAsync(
        OrchestratorWorkspace canonical,
        CancellationToken cancellationToken = default)
    {
        var legacyDir = Path.GetFullPath(
            Path.Combine(canonical.RootDirectory, "src", "Mcg.AgentOrchestrator.App", ".orchestrator"));
        var canonicalDir = Path.GetFullPath(canonical.OrchestratorDirectory);

        if (string.Equals(legacyDir, canonicalDir, StringComparison.OrdinalIgnoreCase))
            return ConsolidationResult.NoOp("Legacy and canonical paths are the same directory.");

        var legacyStateDb = Path.Combine(legacyDir, "state.db");
        if (!File.Exists(legacyStateDb))
            return ConsolidationResult.NoOp($"No legacy state.db at {legacyStateDb}.");

        var legacyRepo = new SqliteOrchestratorStateRepository(legacyStateDb);
        var canonicalRepo = new SqliteOrchestratorStateRepository(canonical.SqliteStatePath);

        var legacyMeta = (await legacyRepo.ListGoalMetadataAsync(cancellationToken))
            .ToDictionary(g => g.Id, g => g.UpdatedAt);
        var canonicalMeta = (await canonicalRepo.ListGoalMetadataAsync(cancellationToken))
            .ToDictionary(g => g.Id, g => g.UpdatedAt);

        var legacyKernel = await legacyRepo.LoadAsync(cancellationToken);
        var canonicalKernel = await canonicalRepo.LoadAsync(cancellationToken);

        var legacySnapshot = legacyKernel.ExportSnapshot();
        var canonicalSnapshot = canonicalKernel.ExportSnapshot();

        var merged = new List<string>();
        var skipped = new List<string>();

        var mergedGoals = new List<GoalSnapshot>(canonicalSnapshot.Goals);

        foreach (var legacyGoal in legacySnapshot.Goals)
        {
            if (canonicalMeta.TryGetValue(legacyGoal.Id, out var canonicalTs))
            {
                var legacyTs = legacyMeta.GetValueOrDefault(legacyGoal.Id, string.Empty);
                if (string.CompareOrdinal(canonicalTs, legacyTs) >= 0)
                {
                    skipped.Add(legacyGoal.Id);
                    Console.Error.WriteLine(
                        $"[workspace-consolidate] Skipped goal {legacyGoal.Id[..8]}: canonical ({canonicalTs}) >= legacy ({legacyTs}).");
                    continue;
                }
                // Legacy record is newer — replace the canonical entry.
                mergedGoals.RemoveAll(g => g.Id == legacyGoal.Id);
            }

            mergedGoals.Add(legacyGoal);
            merged.Add(legacyGoal.Id);
            Console.Error.WriteLine(
                $"[workspace-consolidate] Merged goal {legacyGoal.Id[..8]} " +
                $"(legacy updated_at: {legacyMeta.GetValueOrDefault(legacyGoal.Id, "unknown")}).");
        }

        var mergedSnapshot = new OrchestratorSnapshot(mergedGoals, canonicalSnapshot.HumanInputRequests);
        var mergedKernel = AgentOrchestratorKernel.FromSnapshot(mergedSnapshot);
        await canonicalRepo.SaveAsync(mergedKernel, cancellationToken);

        // Release pooled SQLite connections so the file handles are freed before moving the directory.
        SqliteConnection.ClearAllPools();

        var datestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var archiveName = $".orchestrator-legacy-{datestamp}";
        var archivePath = Path.Combine(
            canonical.RootDirectory, "src", "Mcg.AgentOrchestrator.App", archiveName);
        Directory.Move(legacyDir, archivePath);

        Console.Error.WriteLine(
            $"[workspace-consolidate] Legacy directory renamed to {archivePath}. " +
            $"Merged {merged.Count} goal(s), skipped {skipped.Count}.");

        return new ConsolidationResult(false, null, merged, skipped, archivePath);
    }
}
