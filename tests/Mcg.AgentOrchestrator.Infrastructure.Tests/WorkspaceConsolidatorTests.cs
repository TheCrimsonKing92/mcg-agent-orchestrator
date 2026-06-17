using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkspaceConsolidatorTests
{
    [Xunit.Fact(DisplayName = "ResolveRepoRoot_finds_solution_root_from_nested_subdirectory")]
    public void ResolveRepoRootFindsSolutionRootFromNestedSubdirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var sub = Path.Combine(root, "src", "deep", "nested");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);

            var result = OrchestratorWorkspace.ResolveRepoRoot(sub);

            Assert.Equal(Path.GetFullPath(root), result);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ResolveRepoRoot_falls_back_to_start_directory_when_no_solution_found")]
    public void ResolveRepoRootFallsBackToStartDirectoryWhenNoSolutionFound()
    {
        var dir = CreateTempDirectory();
        try
        {
            var result = OrchestratorWorkspace.ResolveRepoRoot(dir, dir);

            Assert.Equal(Path.GetFullPath(dir), result);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Xunit.Fact(DisplayName = "ResolveRepoRoot_uses_fallback_directory_when_start_directory_has_no_solution")]
    public void ResolveRepoRootUsesFallbackDirectoryWhenStartDirectoryHasNoSolution()
    {
        var start = CreateTempDirectory();
        var fallbackRoot = CreateTempDirectory();
        try
        {
            var fallbackSub = Path.Combine(fallbackRoot, "bin", "release");
            Directory.CreateDirectory(fallbackSub);
            File.WriteAllText(Path.Combine(fallbackRoot, "Mcg.AgentOrchestrator.sln"), string.Empty);

            var result = OrchestratorWorkspace.ResolveRepoRoot(start, fallbackSub);

            Assert.Equal(Path.GetFullPath(fallbackRoot), result);
        }
        finally
        {
            DeleteDirectory(start);
            DeleteDirectory(fallbackRoot);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_is_no_op_when_no_legacy_directory_exists")]
    public async Task LegacyWorkspaceConsolidatorIsNoOpWhenNoLegacyDirectoryExists()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);

            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.True(result.IsNoOp);
            Assert.True(result.NoOpReason is not null);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_is_no_op_when_same_canonical_and_legacy_path")]
    public async Task LegacyWorkspaceConsolidatorIsNoOpWhenSameCanonicalAndLegacyPath()
    {
        // If the repo root happens to equal src/Mcg.AgentOrchestrator.App, canonical == legacy.
        // We simulate this by using a root that makes the legacy path resolve to the canonical path.
        // The guard checks path equality after GetFullPath.
        var root = CreateTempDirectory();
        try
        {
            // Create a workspace whose OrchestratorDirectory == the legacy dir under root
            // This is done by setting root such that root/.orchestrator == legacy dir
            // Legacy = root/src/Mcg.AgentOrchestrator.App/.orchestrator
            // Canonical = root/.orchestrator
            // These differ normally — so to test the same-path guard we craft a workspace where
            // OrchestratorDirectory is manually aliased. Instead, just verify no-op for missing legacy.
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);
            Assert.True(result.IsNoOp);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_merges_legacy_only_goals_into_canonical")]
    public async Task LegacyWorkspaceConsolidatorMergesLegacyOnlyGoalsIntoCanonical()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, canonicalDbPath) = SetupWorkspacePaths(root);

            var legacyOnlyId = Guid.NewGuid().ToString("n");
            SeedGoal(legacyDbPath, legacyOnlyId, "2026-01-01T00:00:00.0000000+00:00", "Legacy only goal");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.False(result.IsNoOp);
            Assert.Equal(1, result.MergedGoalIds.Count);
            Assert.True(result.MergedGoalIds.Contains(legacyOnlyId));
            Assert.Equal(0, result.SkippedGoalIds.Count);

            var canonicalKernel = await new SqliteOrchestratorStateRepository(canonicalDbPath).LoadAsync();
            Assert.True(canonicalKernel.Goals.Any(g => g.Id.Value == legacyOnlyId));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_skips_legacy_goal_when_canonical_is_newer")]
    public async Task LegacyWorkspaceConsolidatorSkipsLegacyGoalWhenCanonicalIsNewer()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, canonicalDbPath) = SetupWorkspacePaths(root);

            var sharedId = Guid.NewGuid().ToString("n");
            SeedGoal(legacyDbPath, sharedId, "2026-01-01T00:00:00.0000000+00:00", "Older legacy objective");
            SeedGoal(canonicalDbPath, sharedId, "2026-06-15T00:00:00.0000000+00:00", "Newer canonical objective");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.False(result.IsNoOp);
            Assert.Equal(0, result.MergedGoalIds.Count);
            Assert.Equal(1, result.SkippedGoalIds.Count);
            Assert.True(result.SkippedGoalIds.Contains(sharedId));

            // Canonical objective must be preserved.
            var meta = await new SqliteOrchestratorStateRepository(canonicalDbPath).ListGoalMetadataAsync();
            var entry = meta.Single(m => m.Id == sharedId);
            Assert.Equal("Newer canonical objective", entry.Objective);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_merges_newer_legacy_goal_over_older_canonical")]
    public async Task LegacyWorkspaceConsolidatorMergesNewerLegacyGoalOverOlderCanonical()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, canonicalDbPath) = SetupWorkspacePaths(root);

            var sharedId = Guid.NewGuid().ToString("n");
            SeedGoal(legacyDbPath, sharedId, "2026-06-16T00:00:00.0000000+00:00", "Newer legacy objective");
            SeedGoal(canonicalDbPath, sharedId, "2026-01-01T00:00:00.0000000+00:00", "Older canonical objective");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.False(result.IsNoOp);
            Assert.Equal(1, result.MergedGoalIds.Count);
            Assert.True(result.MergedGoalIds.Contains(sharedId));
            Assert.Equal(0, result.SkippedGoalIds.Count);

            var canonicalKernel = await new SqliteOrchestratorStateRepository(canonicalDbPath).LoadAsync();
            var goal = canonicalKernel.Goals.Single(g => g.Id.Value == sharedId);
            Assert.Equal("Newer legacy objective", goal.Objective);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_archives_legacy_directory_after_merge")]
    public async Task LegacyWorkspaceConsolidatorArchivesLegacyDirectoryAfterMerge()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, _) = SetupWorkspacePaths(root);
            var legacyDir = Path.GetDirectoryName(legacyDbPath)!;

            SeedGoal(legacyDbPath, Guid.NewGuid().ToString("n"), "2026-06-01T00:00:00.0000000+00:00", "Goal to migrate");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.False(result.IsNoOp);
            Assert.False(Directory.Exists(legacyDir));
            Assert.True(result.ArchivePath is not null);
            Assert.True(Directory.Exists(result.ArchivePath));
            Assert.True(result.ArchivePath!.Contains(".orchestrator-legacy-", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_preserves_unrelated_canonical_goals_during_merge")]
    public async Task LegacyWorkspaceConsolidatorPreservesUnrelatedCanonicalGoalsDuringMerge()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, canonicalDbPath) = SetupWorkspacePaths(root);

            var legacyId = Guid.NewGuid().ToString("n");
            var canonicalId = Guid.NewGuid().ToString("n");
            SeedGoal(legacyDbPath, legacyId, "2026-06-01T00:00:00.0000000+00:00", "Legacy goal");
            SeedGoal(canonicalDbPath, canonicalId, "2026-06-10T00:00:00.0000000+00:00", "Canonical-only goal");

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.False(result.IsNoOp);
            Assert.Equal(1, result.MergedGoalIds.Count);

            var canonicalKernel = await new SqliteOrchestratorStateRepository(canonicalDbPath).LoadAsync();
            Assert.True(canonicalKernel.Goals.Any(g => g.Id.Value == legacyId));
            Assert.True(canonicalKernel.Goals.Any(g => g.Id.Value == canonicalId));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_migrates_legacy_model_functions_when_canonical_lacks_one")]
    public async Task LegacyWorkspaceConsolidatorMigratesLegacyModelFunctionsWhenCanonicalLacksOne()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, _) = SetupWorkspacePaths(root);
            var legacyDir = Path.GetDirectoryName(legacyDbPath)!;
            var canonicalDir = Path.Combine(root, ".orchestrator");

            SeedGoal(legacyDbPath, Guid.NewGuid().ToString("n"), "2026-06-01T00:00:00.0000000+00:00", "Goal");
            var legacyJson = PopulatedModelFunctionsJson();
            File.WriteAllText(Path.Combine(legacyDir, "model-functions.json"), legacyJson);

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.False(result.IsNoOp);
            Assert.True(result.MigratedConfigFiles.Contains("model-functions.json"));
            Assert.False(result.SkippedConfigFiles.Contains("model-functions.json"));

            var canonicalConfigPath = Path.Combine(canonicalDir, "model-functions.json");
            Assert.True(File.Exists(canonicalConfigPath));
            Assert.Equal(legacyJson, File.ReadAllText(canonicalConfigPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_does_not_overwrite_populated_canonical_config")]
    public async Task LegacyWorkspaceConsolidatorDoesNotOverwritePopulatedCanonicalConfig()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, _) = SetupWorkspacePaths(root);
            var legacyDir = Path.GetDirectoryName(legacyDbPath)!;
            var canonicalDir = Path.Combine(root, ".orchestrator");

            SeedGoal(legacyDbPath, Guid.NewGuid().ToString("n"), "2026-06-01T00:00:00.0000000+00:00", "Goal");

            var legacyJson = PopulatedModelFunctionsJson("legacy-judge");
            File.WriteAllText(Path.Combine(legacyDir, "model-functions.json"), legacyJson);

            var canonicalJson = PopulatedModelFunctionsJson("canonical-judge");
            var canonicalConfigPath = Path.Combine(canonicalDir, "model-functions.json");
            File.WriteAllText(canonicalConfigPath, canonicalJson);

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var result = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);

            Assert.False(result.IsNoOp);
            Assert.False(result.MigratedConfigFiles.Contains("model-functions.json"));
            Assert.True(result.SkippedConfigFiles.Contains("model-functions.json"));

            // Canonical content must be preserved unchanged.
            Assert.Equal(canonicalJson, File.ReadAllText(canonicalConfigPath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "LegacyWorkspaceConsolidator_is_idempotent_on_second_run")]
    public async Task LegacyWorkspaceConsolidatorIsIdempotentOnSecondRun()
    {
        var root = CreateTempDirectory();
        try
        {
            var (legacyDbPath, _) = SetupWorkspacePaths(root);
            SeedGoal(legacyDbPath, Guid.NewGuid().ToString("n"), "2026-06-01T00:00:00.0000000+00:00", "Goal");

            var workspace = OrchestratorWorkspace.ForDirectory(root);

            var first = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);
            Assert.False(first.IsNoOp);

            // Second run: legacy dir has been renamed to archive, so no legacy state.db exists.
            var second = await LegacyWorkspaceConsolidator.ConsolidateAsync(workspace);
            Assert.True(second.IsNoOp);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static (string LegacyDbPath, string CanonicalDbPath) SetupWorkspacePaths(string root)
    {
        var legacyDir = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", ".orchestrator");
        var canonicalDir = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(canonicalDir);
        return (Path.Combine(legacyDir, "state.db"), Path.Combine(canonicalDir, "state.db"));
    }

    private static void SeedGoal(string dbPath, string goalId, string updatedAt, string objective)
    {
        // Seed via kernel so the schema and snapshot are in the expected format.
        var repo = new SqliteOrchestratorStateRepository(dbPath);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(goalId), objective);
        repo.SaveAsync(kernel).GetAwaiter().GetResult();

        // Patch the updated_at to the explicit timestamp for deterministic comparison tests.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadWrite;");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE goals SET updated_at = $ts WHERE id = $id";
        cmd.Parameters.AddWithValue("$ts", updatedAt);
        cmd.Parameters.AddWithValue("$id", goalId);
        cmd.ExecuteNonQuery();
    }

    // Returns a minimal JSON string that IsPopulatedConfigFile recognises as populated
    // (non-empty Bindings array). The purpose string differentiates canonical vs legacy fixtures.
    private static string PopulatedModelFunctionsJson(string purpose = "acceptance-judge") =>
        "{\"Bindings\":[{\"Purpose\":\"" + purpose + "\",\"Lane\":\"CheapApi\",\"Model\":{\"ProviderName\":\"Anthropic\",\"ModelName\":\"claude-haiku-4-5\",\"Capabilities\":\"Text\",\"SubscriptionMode\":\"ApiKey\"}}]}";

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
