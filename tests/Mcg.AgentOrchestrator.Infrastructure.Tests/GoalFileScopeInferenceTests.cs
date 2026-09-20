using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalFileScopeInferenceTests
{
    [Xunit.Theory]
    [Xunit.InlineData(
        "Do not change WorkerProcessJobs.cs. Its diagnostics are already correct.",
        "src/Mcg.AgentOrchestrator.Infrastructure/Processes/WorkerProcessJobs.cs")]
    [Xunit.InlineData(
        "Do not change config/acceptance-manifest.json. A manifest edit forces a full gate and a cache wipe.",
        "config/acceptance-manifest.json")]
    [Xunit.InlineData(
        "This is test-only work. Do not change ConductorDriver.cs.",
        "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs")]
    public void ProhibitionClause_PathOnlyOccurrence_IsNotDeclared(
        string receiptSentence,
        string repositoryPath)
    {
        var objective = $"{receiptSentence}\nDo not change {repositoryPath}.";
        var positiveControl = objective.Replace("Do not change", "Change", StringComparison.Ordinal);

        var scopes = GoalFileScopeInference.FromText(objective);
        var positiveScopes = GoalFileScopeInference.FromText(positiveControl);

        Xunit.Assert.DoesNotContain(scopes, scope => scope.Path.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.Contains(positiveScopes, scope => scope.Path.Equals(repositoryPath, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<object[]> ProhibitionVocabulary()
    {
        foreach (var opener in new[] { "do not", "don't", "must not", "never" })
        {
            foreach (var verb in new[] { "touch", "change", "modify", "edit", "alter", "rename", "delete" })
            {
                yield return [opener, verb];
            }
        }
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(ProhibitionVocabulary))]
    public void ProhibitionClause_RecognizedVocabulary_SuppressesOccurrence(
        string opener,
        string verb)
    {
        var scopes = GoalFileScopeInference.FromText($"{opener} {verb} src/Feature/File.cs.");

        Xunit.Assert.Empty(scopes);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PositiveOccurrence_AlsoProhibited_RemainsDeclared(bool prohibitionFirst)
    {
        const string path = "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs";
        var positive = $"Expected to change:\n- {path}";
        var prohibition = $"Do not change {path}.";
        var objective = prohibitionFirst
            ? $"{prohibition}\n{positive}"
            : $"{positive}\n{prohibition}";

        var scope = Xunit.Assert.Single(GoalFileScopeInference.FromText(objective));

        Xunit.Assert.Equal(path, scope.Path);
        Xunit.Assert.Equal(FileScopeProvenance.Explicit, scope.Provenance);
    }

    public static IEnumerable<object[]> DeclarationShapes()
    {
        yield return
        [
            "Expected to change:\n- tests/Mcg.AgentOrchestrator.Infrastructure.Tests\n- tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalRefinementTests.cs",
            new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalRefinementTests.cs" }
        ];
        yield return
        [
            "Expected to change:\n- tests/Mcg.AgentOrchestrator.Infrastructure.Tests",
            new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests" }
        ];
        yield return
        [
            string.Join(
                "\n",
                "Inspect tests/Mcg.AgentOrchestrator.Infrastructure.Tests.",
                BacklogIntakePlanner.TargetScopeHeadingLine,
                BacklogIntakePlanner.UnknownScopeMarkerLine,
                BacklogIntakePlanner.ScopeIncludesHeadingLine,
                "- tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalRefinementTests.cs"),
            new[]
            {
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalRefinementTests.cs"
            }
        ];
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(DeclarationShapes))]
    public void DeclarationShape_RedundantAncestor_IsPruned(
        string objective,
        string[] expectedPaths)
    {
        var scopes = GoalFileScopeInference.FromText(objective);

        Xunit.Assert.Equal(expectedPaths, scopes.Select(scope => scope.Path));
    }

    [Xunit.Fact]
    public void DeriveForIntake_DoNotChange_ProducesExclusion()
    {
        var root = CreateRepository();
        try
        {
            var result = GoalFileScopeInference.DeriveForIntake(
                "Touch only src/Feature/File.cs; do not change src/Feature/Generated.cs.",
                root);

            Xunit.Assert.Equal(["src/Feature/File.cs"], result.Includes);
            Xunit.Assert.Equal(["src/Feature/Generated.cs"], result.Exclusions);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public void DeriveForIntake_UnresolvableProhibitionTarget_DoesNotEraseValidIncludes()
    {
        var root = CreateRepository();
        try
        {
            var result = GoalFileScopeInference.DeriveForIntake(
                "Update scripts/Invoke-IsolatedDotnet.ps1. " +
                "Do not change WorkerProcessJobs.cs. Its diagnostics are already correct.",
                root);

            Xunit.Assert.Equal(RepositoryScopeConfidence.Precise, result.Confidence);
            Xunit.Assert.Equal(["scripts/Invoke-IsolatedDotnet.ps1"], result.Includes);
            Xunit.Assert.Empty(result.Exclusions);
            Xunit.Assert.Empty(result.Warnings);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public void DeriveForIntake_AmbiguousExclusion_PreservesUnknownConfidence()
    {
        var root = CreateRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "FeatureA", "Rendering"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "FeatureB", "Rendering"));

            var result = GoalFileScopeInference.DeriveForIntake(
                "Update scripts/Invoke-IsolatedDotnet.ps1. Do not touch Rendering.",
                root);

            Xunit.Assert.Equal(RepositoryScopeConfidence.Unknown, result.Confidence);
            Xunit.Assert.Equal(["scripts/Invoke-IsolatedDotnet.ps1"], result.Includes);
            Xunit.Assert.Empty(result.Exclusions);
            Xunit.Assert.Contains(
                result.Warnings,
                warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

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
            SharedTestSupport.RemoveTempDirectory(root);
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
            SharedTestSupport.RemoveTempDirectory(root);
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
            SharedTestSupport.RemoveTempDirectory(root);
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
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalFileScopeInference_reuses_excluded_directory_index_and_rejects_ambiguous_names")]
    public void ReusesExcludedDirectoryIndexAndRejectsAmbiguousNames()
    {
        var root = CreateRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "FeatureA", "Rendering"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "FeatureB", "Rendering"));
            Directory.CreateDirectory(Path.Combine(
                root,
                ".orchestrator-worktrees",
                "copy",
                "src",
                "Mcg.AgentOrchestrator.Infrastructure",
                "OperatorComms"));
            var context = new GoalFileScopeDerivationContext(root);

            var first = GoalFileScopeInference.DeriveForIntake("Touch only OperatorComms.", context);
            var second = GoalFileScopeInference.DeriveForIntake("Touch only Rendering.", context);

            Xunit.Assert.Equal(RepositoryScopeConfidence.Precise, first.Confidence);
            Xunit.Assert.Equal(
                ["src/Mcg.AgentOrchestrator.Infrastructure/OperatorComms"],
                first.Includes);
            Xunit.Assert.Equal(RepositoryScopeConfidence.Unknown, second.Confidence);
            Xunit.Assert.Empty(second.Includes);
            Xunit.Assert.Contains(
                second.Warnings,
                warning => warning.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
            Xunit.Assert.Equal(1, context.DirectoryEnumerationCount);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "GoalFileScopeInference_boilerplate_lint_requires_an_unattributed_exact_set")]
    public void BoilerplateLintRequiresAnUnattributedExactSet()
    {
        string[] boilerplate =
        [
            "src/Mcg.AgentOrchestrator.App/Cli",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workers",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces",
            "scripts/Invoke-IsolatedDotnet.ps1"
        ];

        var unattributedRisks = BacklogIntakePlanner.ApplyScopeLintRisks(
            "Improve Steward composition behavior.",
            boilerplate,
            ["routine"]);
        var attributedRisks = BacklogIntakePlanner.ApplyScopeLintRisks(
            "Update scripts/Invoke-IsolatedDotnet.ps1.",
            boilerplate,
            ["routine"]);
        var nonExactRisks = BacklogIntakePlanner.ApplyScopeLintRisks(
            "Improve Steward composition behavior.",
            boilerplate[..^1],
            ["routine"]);

        Xunit.Assert.Equal([BacklogIntakePlanner.BoilerplateScopeRiskLabel], unattributedRisks);
        Xunit.Assert.Equal(["routine"], attributedRisks);
        Xunit.Assert.Equal(["routine"], nonExactRisks);
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
            SharedTestSupport.RemoveTempDirectory(root);
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
