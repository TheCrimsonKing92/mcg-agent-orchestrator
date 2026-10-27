using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: filesystem cases own GUID roots and clean them in finally blocks.
public sealed class BriefScopeSectionSelectorTests
{
    internal const string FixtureA = """
        - `src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner.cs` plans every changed path with the orchestrator's own test projects.
        - The new test writes the fixture files `src/Domain/Widget.cs` and `src/index.ts`, and asserts that no check names `tests/Mcg.`.
        ## Scope
        Files this goal may change:
        - the new `src/Mcg.AgentOrchestrator.Core/Application/ForeignTargetTestImpactPlanner.cs`;
        - the new `src/Mcg.AgentOrchestrator.Execution/Workers/WorkerTestImpactPlanSelection.cs`;
        - line 96 of `src/Mcg.AgentOrchestrator.Execution/Workers/WorkerArtifactWriter.cs`;
        - the two new test files in `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/`.
        Out of scope:
        - `CandidateTreeProbe` rules for the home repository.
        """;

    internal const string FixtureB = """
        - `src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.SharedGoalRoots.cs:83-113` reads every store.
        - The 27 lanes sum to about 110 serial minutes in config/acceptance-manifest.json.
        ## Scope
        Files this goal may change:
        - the new `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/UnstoredGoalBuildRootRetention.cs`, `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/UnstoredGoalBuildRootKeepReason.cs` and `src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/UnstoredGoalBuildRootRetentionResult.cs`;
        - `src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.OwnedRoots.cs`, only for What to build item 3;
        - line 107 of `src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.ConductEvents.cs`;
        - line 762 of `docs/operator-runbook.md`;
        - the new `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/UnstoredGoalBuildRootRetentionTests.cs` and `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/UnstoredGoalBuildRootRetentionScenarioTests.cs`.
        Out of scope:
        - `SourceSizeRatchet.cs`, `config/acceptance-manifest.json` and every existing test file.
        """;

    internal const string FixtureC = """
        - The record writer fills `/results/tests/0/status` and `/results/tests/1/testId` for each case.
        - The fixture has test ids `tests/test_parse.py::test_ok` and `src/view.test.ts::renders`.
        ## Scope
        Files this goal may change:
        - `record/record.go`;
        - `cmd/crosswalk/main.go`, only the flag parsing.
        Out of scope:
        - the `tests/test_io.py` fixtures.
        """;

    internal const string FixtureD0 = """
        - The record writer fills `/results/tests/0/status` and `/results/tests/1/testId` for each case.
        - The fixture has test ids `tests/test_parse.py::test_ok` and `src/view.test.ts::renders`.
        ## Scope
        Files this goal may change:
        - `record/record.go`;
        - the new `cmd/crosswalk/main.go`;
        - `cmd/crosswalk/flags.go`;
        Out of scope:
        - the `tests/test_io.py` fixtures.
        """;

    internal static readonly string[] FixtureBPaths =
    [
        "docs/operator-runbook.md",
        "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.ConductEvents.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.OwnedRoots.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/UnstoredGoalBuildRootKeepReason.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/UnstoredGoalBuildRootRetention.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/UnstoredGoalBuildRootRetentionResult.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/UnstoredGoalBuildRootRetentionScenarioTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/UnstoredGoalBuildRootRetentionTests.cs"
    ];

    [Theory]
    [InlineData(FixtureA, 4)]
    [InlineData(FixtureB, 8)]
    public void FromText_ScopeRegion_SelectsOnlyListedExplicitPaths(string text, int count)
    {
        var scopeLines = text.Split("Files this goal may change:", StringSplitOptions.None)[1]
            .Split("Out of scope:", StringSplitOptions.None)[0];
        var scopes = GoalFileScopeInference.FromText(text);

        Assert.Equal(GoalFileScopeInference.FromText(scopeLines).Select(scope => scope.Path),
            scopes.Select(scope => scope.Path));
        Assert.Equal(count, scopes.Count);
        Assert.All(scopes, scope => Assert.Equal(FileScopeProvenance.Explicit, scope.Provenance));
        var excluded = new[]
        {
            "src/Mcg.AgentOrchestrator.Core/Application/RepositoryTestImpactPlanner.cs",
            "src/Domain/Widget.cs", "src/index.ts", "tests/Mcg",
            "src/Mcg.AgentOrchestrator.App/Orchestration/TerminalGoalSweep.SharedGoalRoots.cs",
            "config/acceptance-manifest.json"
        };
        Assert.All(excluded, path => Assert.DoesNotContain(scopes, scope => scope.Path == path));
        if (text == FixtureB)
        {
            Assert.Equal(FixtureBPaths, scopes.Select(scope => scope.Path));
        }
    }

    [Fact]
    public void FromText_ForeignPaths_UsesScopeListAndRejectsPremiseEvidence()
    {
        var scopes = GoalFileScopeInference.FromText(FixtureC);

        Assert.Equal(["cmd/crosswalk/main.go", "record/record.go"], scopes.Select(scope => scope.Path));
        Assert.All(scopes, scope => Assert.Equal(FileScopeProvenance.Explicit, scope.Provenance));
        Assert.Empty(GoalFileScopeInference.FromText(FixtureC.Split("## Scope")[0]));
    }

    [Theory]
    [InlineData("Read /results/tests/0/status.")]
    [InlineData("Run tests/test_parse.py::test_ok.")]
    [InlineData("Build tests/Mcg.AgentOrchestrator.*.csproj")]
    [InlineData("Change src/Feature/File?.cs.")]
    [InlineData("Inspect tests/0/name.")]
    [InlineData("Open C:\\repo\\project\\src\\Feature\\File.cs.")]
    public void FromText_EvidenceOnlyText_DeclaresNoScope(string text)
    {
        Assert.Empty(GoalFileScopeInference.FromText(text));
    }

    [Theory]
    [InlineData("Change src/Feature/File.cs.", "src/Feature/File.cs")]
    [InlineData("Change tests/Feature2/File10.cs.", "tests/Feature2/File10.cs")]
    public void FromText_OrdinaryPath_RemainsExplicit(string text, string path)
    {
        Assert.Equal(new DeclaredFileScope(path, FileScopeProvenance.Explicit),
            Assert.Single(GoalFileScopeInference.FromText(text)));
    }

    [Fact]
    public void FromText_GeneratedGlob_RetainsDerivedProvenance()
    {
        var text = string.Join('\n', BacklogIntakePlanner.TargetScopeHeadingLine,
            BacklogIntakePlanner.PreciseScopeMarkerLine, BacklogIntakePlanner.ScopeIncludesHeadingLine,
            "- tests/NewArea/*.cs");

        Assert.Equal(new DeclaredFileScope("tests/NewArea/*.cs", FileScopeProvenance.Derived),
            Assert.Single(GoalFileScopeInference.FromText(text)));
    }

    [Fact]
    public void KeepResolvable_RootsDiffer_KeepsExistingNewAndGeneratedPaths()
    {
        var root1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var root2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root1, "record"));
        Directory.CreateDirectory(root2);
        try
        {
            File.WriteAllText(Path.Combine(root1, "record/record.go"), "package record");
            var fixtureD = FixtureD0 + "\n" + string.Join('\n',
                BacklogIntakePlanner.TargetScopeHeadingLine, BacklogIntakePlanner.UnknownScopeMarkerLine,
                BacklogIntakePlanner.ScopeIncludesHeadingLine, "- src/Generated/Only.cs");
            var scopes = GoalFileScopeInference.FromText(fixtureD);
            Assert.Contains(scopes, scope => scope.Path == "cmd/crosswalk/flags.go");

            var inRoot1 = BriefScopeSectionSelector.KeepResolvable(fixtureD, scopes, root1);
            var inRoot2 = BriefScopeSectionSelector.KeepResolvable(fixtureD, scopes, root2);

            Assert.Equal(["cmd/crosswalk/main.go", "record/record.go", "src/Generated/Only.cs"],
                inRoot1.Select(scope => scope.Path));
            Assert.Equal(["cmd/crosswalk/main.go", "src/Generated/Only.cs"],
                inRoot2.Select(scope => scope.Path));
            Assert.Equal(FileScopeProvenance.Inferred,
                Assert.Single(inRoot2, scope => scope.Path == "src/Generated/Only.cs").Provenance);
        }
        finally
        {
            Directory.Delete(root1, true);
            Directory.Delete(root2, true);
        }
    }

    [Fact]
    public void KeepResolvable_NoRegion_UsesExistingNewDeclarationRule()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string text = "Add src/Feature/NewFile.cs and inspect src/Feature/Missing.cs.";

            Assert.Equal(["src/Feature/NewFile.cs"], BriefScopeSectionSelector.KeepResolvable(
                text, GoalFileScopeInference.FromText(text), root).Select(scope => scope.Path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("### Next")]
    [InlineData("out OF scope:")]
    [InlineData("Post-landing check:")]
    [InlineData("Dependencies: none")]
    [InlineData("Verification ownership:")]
    [InlineData(BacklogIntakePlanner.TargetScopeHeadingLine)]
    public void FindScopeRegion_Terminator_EndsFirstRegion(string terminator)
    {
        string[] lines = ["# Premise", "## sCoPe", "- `record/record.go`", "  " + terminator,
            "## Scope", "- `cmd/crosswalk/main.go`"];

        Assert.Equal((2, 3), BriefScopeSectionSelector.FindScopeRegion(lines));
    }

    [Fact]
    public void FindScopeRegion_NoTerminator_EndsAtInputLength()
    {
        Assert.Equal((1, 2), BriefScopeSectionSelector.FindScopeRegion(["# Scope", "file"]));
        Assert.Null(BriefScopeSectionSelector.FindScopeRegion(["Scope", "file"]));
    }

    [Theory]
    [InlineData("`record/record.go.,;:)`", "record/record.go")]
    [InlineData("`cmd/crosswalk/`", "cmd/crosswalk/")]
    [InlineData("`/record/record.go`", null)]
    [InlineData("`record/../record.go`", null)]
    [InlineData("`record/./record.go`", null)]
    [InlineData("`record/12/record.go`", null)]
    [InlineData("`record/record.go::test`", null)]
    [InlineData("`record/*.go`", null)]
    [InlineData("`record/file name.go`", null)]
    [InlineData("`record/noextension`", null)]
    public void BacktickedPaths_Candidate_EnforcesRepositoryPathShape(string text, string? path)
    {
        Assert.Equal(path is null ? Array.Empty<string>() : [path],
            BriefScopeSectionSelector.BacktickedPaths(text));
    }
}
