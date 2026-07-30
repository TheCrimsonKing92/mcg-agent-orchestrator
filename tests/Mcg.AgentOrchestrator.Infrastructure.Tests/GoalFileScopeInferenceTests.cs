using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalFileScopeInferenceTests
{
    [Xunit.Fact(DisplayName = "GoalFileScopeInference_explicit_only_and_exclusions_are_authoritative")]
    public void ExplicitOnlyAndExclusionsAreAuthoritative()
    {
        var root = CreateRepository();
        try
        {
            var result = GoalFileScopeInference.DeriveForIntake(
                "Changes must touch ONLY OperatorComms; do NOT touch App/Cli or Workers.",
                root);

            Xunit.Assert.Equal(RepositoryScopeConfidence.Precise, result.Confidence);
            Xunit.Assert.Equal(
                ["src/Mcg.AgentOrchestrator.Infrastructure/OperatorComms"],
                result.Includes);
            Xunit.Assert.Equal(
                [
                    "src/Mcg.AgentOrchestrator.App/Cli",
                    "src/Mcg.AgentOrchestrator.Infrastructure/Workers"
                ],
                result.Exclusions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalFileScopeInference_accepts_existing_declared_new_and_glob_paths")]
    public void AcceptsExistingDeclaredNewAndGlobPaths()
    {
        var root = CreateRepository();
        try
        {
            var body =
                "Update scripts\\Invoke-IsolatedDotnet.ps1. " +
                "Add src/NewArea/NewFile.cs and tests/NewArea/*.cs.";

            var first = GoalFileScopeInference.DeriveForIntake(body, root);
            var second = GoalFileScopeInference.DeriveForIntake(body, root);

            Xunit.Assert.Equal(first, second);
            Xunit.Assert.Equal(RepositoryScopeConfidence.Precise, first.Confidence);
            Xunit.Assert.Equal(
                [
                    "scripts/Invoke-IsolatedDotnet.ps1",
                    "src/NewArea/NewFile.cs",
                    "tests/NewArea/*.cs"
                ],
                first.Includes);
            Xunit.Assert.DoesNotContain(first.Includes, path => path.Contains("...", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalFileScopeInference_no_signal_and_traversal_are_unknown")]
    public void NoSignalAndTraversalAreUnknown()
    {
        var root = CreateRepository();
        try
        {
            var noSignal = GoalFileScopeInference.DeriveForIntake(
                "Improve the operator experience without a declared implementation seam.",
                root);
            var traversal = GoalFileScopeInference.DeriveForIntake(
                "Update ../outside/Secrets.cs.",
                root);
            var placeholder = GoalFileScopeInference.DeriveForIntake(
                "Touch only src/.../Workers.",
                root);

            Xunit.Assert.Equal(RepositoryScopeConfidence.Unknown, noSignal.Confidence);
            Xunit.Assert.Empty(noSignal.Includes);
            Xunit.Assert.Equal(RepositoryScopeConfidence.Unknown, traversal.Confidence);
            Xunit.Assert.Empty(traversal.Includes);
            Xunit.Assert.NotEmpty(traversal.Warnings);
            Xunit.Assert.Equal(RepositoryScopeConfidence.Unknown, placeholder.Confidence);
            Xunit.Assert.Empty(placeholder.Includes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalFileScopeInference_collapses_parent_and_forbidden_child")]
    public void CollapsesParentAndForbiddenChild()
    {
        var root = CreateRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "Feature"));
            File.WriteAllText(Path.Combine(root, "src", "Feature", "File.cs"), string.Empty);

            var result = GoalFileScopeInference.DeriveForIntake(
                "Touch only src/Feature and src/Feature/File.cs; do not touch src/Feature/Generated.",
                root);

            Xunit.Assert.Equal(["src/Feature"], result.Includes);
            Xunit.Assert.Equal(["src/Feature/Generated"], result.Exclusions);
            Xunit.Assert.Equal(RepositoryScopeConfidence.Precise, result.Confidence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "GoalFileScopeInference_planner_task_scope_overrides_unknown_intake_fallback")]
    public void PlannerTaskScopeOverridesUnknownIntakeFallback()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(
            TaskId.New(),
            "Implement the Core-first increment in src/Mcg.AgentOrchestrator.Core.",
            AgentRole.Developer);
        var objective = string.Join(
            Environment.NewLine,
            "Unresolved evidence mentions src/DoesNotExist.cs.",
            BacklogIntakePlanner.TargetScopeHeadingLine,
            BacklogIntakePlanner.UnknownScopeMarkerLine,
            BacklogIntakePlanner.ScopeIncludesHeadingLine,
            "- none");
        var goal = kernel.CreateGoal(objective, [task]);

        var scope = GoalFileScopeInference.ForScheduling(goal, task);

        Xunit.Assert.Equal(RepositoryScopeConfidence.Precise, scope.Confidence);
        Xunit.Assert.Equal(["src/Mcg.AgentOrchestrator.Core"], scope.Includes);
    }

    [Xunit.Fact(DisplayName = "BacklogIntakePlanner_historical_bodies_emit_distinct_typed_scopes")]
    public async Task HistoricalBodiesEmitDistinctTypedScopes()
    {
        var root = CreateRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var store = new BacklogStore(workspace.BacklogStorePath);
            await store.AddAsync(
                "No-build timing",
                "Measure no-build timing in scripts/Invoke-IsolatedDotnet.ps1.");
            await store.AddAsync(
                "Steward v0",
                "Introduce Steward v0 composition behavior without choosing an implementation seam.");
            await store.AddAsync(
                "Steward integration",
                "Changes must be confined to OperatorComms; do not touch App/Cli or Workers.");

            var plan = BacklogIntakePlanner.Build(workspace.BacklogStorePath, maxItems: 3);
            var items = plan.Items.ToDictionary(item => item.Heading);

            Xunit.Assert.Equal(
                ["scripts/Invoke-IsolatedDotnet.ps1"],
                items["No-build timing"].TargetFiles);
            Xunit.Assert.Equal(RepositoryScopeConfidence.Unknown, items["Steward v0"].ScopeConfidence);
            Xunit.Assert.Empty(items["Steward v0"].TargetFiles);
            Xunit.Assert.Equal(
                ["src/Mcg.AgentOrchestrator.Infrastructure/OperatorComms"],
                items["Steward integration"].TargetFiles);
            Xunit.Assert.Contains(
                BacklogIntakePlanner.UnknownScopeMarkerLine,
                items["Steward v0"].SuggestedObjective,
                StringComparison.Ordinal);
            Xunit.Assert.Contains(
                $"{BacklogIntakePlanner.ScopeExclusionsHeadingLine}{Environment.NewLine}- src/Mcg.AgentOrchestrator.App/Cli",
                items["Steward integration"].SuggestedObjective,
                StringComparison.Ordinal);

            var sets = items.Values
                .Select(item => string.Join("|", item.TargetFiles))
                .ToArray();
            Xunit.Assert.Equal(sets.Length, sets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Xunit.Assert.DoesNotContain(
                items.Values,
                item => GoalFileScopeInference.IsKnownBoilerplateScopeSet(item.TargetFiles));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Cli"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", "OperatorComms"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Mcg.AgentOrchestrator.Infrastructure", "Workers"));
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "Invoke-IsolatedDotnet.ps1"), string.Empty);
        return root;
    }
}
