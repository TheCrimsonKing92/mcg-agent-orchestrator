using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: parser cases are pure; caller cases own GUID roots and clean up in finally.
public sealed class RepositoryNeutralScopePathTests
{
    [Theory]
    [InlineData("cmd/crosswalk/main.go")]
    [InlineData("internal/adapter/list.go")]
    [InlineData("packages/ui/widget.ts")]
    [InlineData("arbitrary-name/component.py")]
    [InlineData("another_root/v2/component.test.ts")]
    [InlineData("team+one/main.go")]
    [InlineData("@scope/widget/index.ts")]
    [InlineData("packages/@scope/widget+test.ts")]
    [InlineData("équipe/entrée.py")]
    [InlineData("team(one)/main.go")]
    [InlineData("team+one/")]
    [InlineData("@scope/widget/")]
    [InlineData("cmd/")]
    [InlineData("internal/adapter/")]
    public void FromText_UnquotedScopeToken_DeclaresExplicitPath(string path)
    {
        // Acceptance holds these assertions constant for baseline RED and candidate GREEN.
        foreach (var line in new[] { path, "- " + path, "  -   " + path + "  " })
        {
            Assert.Equal(new DeclaredFileScope(path, FileScopeProvenance.Explicit),
                Assert.Single(GoalFileScopeInference.FromText("## Scope\r\n" + line)));
        }
    }

    [Theory]
    [InlineData("`cmd/crosswalk/main.go`", "cmd/crosswalk/main.go")]
    [InlineData("- `internal/adapter/`", "internal/adapter/")]
    [InlineData("- the new `packages/ui/widget.ts`;", "packages/ui/widget.ts")]
    [InlineData("src/Feature/File.cs", "src/Feature/File.cs")]
    [InlineData("src/Feature", "src/Feature")]
    [InlineData("src\\Feature\\File.cs", "src/Feature/File.cs")]
    [InlineData("\"src/Feature/File.cs\"", "src/Feature/File.cs")]
    [InlineData("'src/Feature/File.cs'", "src/Feature/File.cs")]
    [InlineData("- line 83 of `docs/operator-runbook.md`;", "docs/operator-runbook.md")]
    public void FromText_ExistingScopeForms_PreserveExplicitPaths(string line, string path)
    {
        Assert.Equal(new DeclaredFileScope(path, FileScopeProvenance.Explicit),
            Assert.Single(GoalFileScopeInference.FromText("## Scope\n" + line)));
    }

    [Theory]
    [InlineData("../cmd/crosswalk/main.go")]
    [InlineData("cmd/../adapter/list.go")]
    [InlineData("cmd/./main.go")]
    [InlineData("/cmd/crosswalk/main.go")]
    [InlineData("C:/cmd/crosswalk/main.go")]
    [InlineData("C:cmd/crosswalk/main.go")]
    [InlineData("//server/cmd/main.go")]
    [InlineData("\\\\server\\cmd\\main.go")]
    [InlineData("https://example.com/cmd/main.go")]
    [InlineData("file:///cmd/main.go")]
    [InlineData("cmd\\crosswalk\\main.go")]
    [InlineData("cmd/main.go::Main")]
    [InlineData("cmd/main.go:83-90")]
    [InlineData("cmd/main.go#L83")]
    [InlineData("@scope/widget/index.ts#L83")]
    [InlineData("team+one/main.go::Main")]
    [InlineData("team+one/main.go:83-90")]
    [InlineData("cmd/*.go")]
    [InlineData("cmd/file?.go")]
    [InlineData("cmd/[ab].go")]
    [InlineData("cmd/{a,b}.go")]
    [InlineData("cmd/12/main.go")]
    [InlineData("main.go")]
    [InlineData("cmd/Makefile")]
    [InlineData("cmd/.env")]
    [InlineData("cmd/main.")]
    [InlineData("cmd//main.go")]
    [InlineData("cmd//")]
    [InlineData("cmd/.../main.go")]
    [InlineData("cmd/main.go;")]
    [InlineData("cmd/main.go,")]
    [InlineData("cmd/main.go)")]
    [InlineData("Change cmd/main.go")]
    [InlineData("cmd/main.go and internal/adapter/list.go")]
    [InlineData("cmd/main.go,internal/adapter/list.go")]
    [InlineData("@scope/widget/index.ts,team+one/main.go")]
    [InlineData("@scope/widget/index.ts;")]
    [InlineData("@scope/widget/index.ts)")]
    [InlineData("1. cmd/main.go")]
    [InlineData("* cmd/main.go")]
    [InlineData("+ cmd/main.go")]
    public void FromText_UnsupportedToken_DeclaresNoScope(string line)
    {
        Assert.Empty(GoalFileScopeInference.FromText("## Scope\n" + line));
    }

    [Theory]
    [InlineData("../src/Feature/File.cs")]
    [InlineData("arbitrary/../src/Feature/File.cs")]
    [InlineData("team+one/../src/Feature/File.cs")]
    [InlineData("/src/Feature/File.cs")]
    [InlineData("C:src/Feature/File.cs")]
    [InlineData("C:/src/Feature/File.cs")]
    [InlineData("//server/src/Feature/File.cs")]
    [InlineData("\\\\server\\src\\Feature\\File.cs")]
    [InlineData("https://example.com/src/Feature/File.cs")]
    [InlineData("foreign::src/Feature/File.cs")]
    [InlineData("foreign;src/Feature/File.cs")]
    [InlineData("foreign,src/Feature/File.cs")]
    [InlineData("foreign#src/Feature/File.cs")]
    [InlineData("foreign/*/src/Feature/File.cs")]
    [InlineData("foreign/0/src/Feature/File.cs")]
    [InlineData("src/Feature/File.cs:83")]
    [InlineData("src/Feature/File.cs::Main")]
    public void FromText_UnsafeWholeToken_DoesNotRecoverHomeRootSuffix(string token)
    {
        foreach (var line in new[] { token, "- " + token })
        {
            Assert.Empty(GoalFileScopeInference.FromText("## Scope\n" + line));
        }
    }

    [Theory]
    [InlineData("cmd/main.go")]
    [InlineData("## File scope\ncmd/main.go")]
    [InlineData("## Evidence\ncmd/main.go")]
    [InlineData("## References\ncmd/main.go")]
    [InlineData("cmd/main.go\n## Scope\nNo paths declared.")]
    [InlineData("## Scope\n## Evidence\ncmd/main.go")]
    [InlineData("## Scope\n### Nested\ncmd/main.go")]
    [InlineData("## Scope\nOut of scope:\ncmd/main.go")]
    [InlineData("## Scope\nPost-landing check:\ncmd/main.go")]
    [InlineData("## Scope\nDependencies: none\ncmd/main.go")]
    [InlineData("## Scope\nVerification ownership:\ncmd/main.go")]
    [InlineData("## Scope\n## Scope\ncmd/main.go")]
    public void FromText_OutsideSelectedRegion_DeclaresNoNeutralScope(string text)
    {
        Assert.Empty(GoalFileScopeInference.FromText(text));
    }

    [Fact]
    public void FromText_Prohibitions_SuppressOnlyTheirOccurrences()
    {
        const string prohibition = "Do not change internal/adapter/list.go.";
        Assert.Empty(GoalFileScopeInference.FromText("## Scope\n" + prohibition));
        Assert.Equal(new DeclaredFileScope("internal/adapter/list.go", FileScopeProvenance.Explicit),
            Assert.Single(GoalFileScopeInference.FromText(
                "## Scope\n- internal/adapter/list.go\n" + prohibition)));
    }

    [Fact]
    public void FromText_AncestorAndChild_PrunesAncestor()
    {
        Assert.Equal(new DeclaredFileScope("internal/adapter/list.go", FileScopeProvenance.Explicit),
            Assert.Single(GoalFileScopeInference.FromText(
                "## Scope\ninternal/adapter/\ninternal/adapter/list.go")));
    }

    [Fact]
    public void FromText_GeneratedBlocks_PreserveTrustAndExclusions()
    {
        var text = string.Join('\n', "## Scope", "cmd/main.go", "src/Feature/File.cs",
            BacklogIntakePlanner.TargetScopeHeadingLine, BacklogIntakePlanner.PreciseScopeMarkerLine,
            BacklogIntakePlanner.ScopeIncludesHeadingLine, "- cmd/generated.go",
            "- src/Feature/File.cs", "- tests/Generated/*.cs",
            BacklogIntakePlanner.ScopeExclusionsHeadingLine, "- src/Forbidden/File.cs");

        Assert.Equal(new[]
        {
            new DeclaredFileScope("cmd/main.go", FileScopeProvenance.Explicit),
            new DeclaredFileScope("src/Feature/File.cs", FileScopeProvenance.Explicit),
            new DeclaredFileScope("tests/Generated/*.cs", FileScopeProvenance.Derived)
        }, GoalFileScopeInference.FromText(text));
    }

    [Fact]
    public void ForScheduling_NeutralTaskScope_OverridesUnknownGoalFallback()
    {
        var task = new TaskSpec(TaskId.New(), "## Scope\ninternal/adapter/list.go", AgentRole.Developer);
        var goal = UnknownGoal(task);

        var scope = GoalFileScopeInference.ForScheduling(goal, task);

        Assert.Equal(["internal/adapter/list.go"], scope.Includes);
        Assert.Equal(RepositoryScopeConfidence.Precise, scope.Confidence);
        Assert.Empty(scope.Warnings);
    }

    [Fact]
    public void ForScheduling_NoExplicitNeutralScope_PreservesUnknownFallback()
    {
        var task = new TaskSpec(TaskId.New(), "Inspect internal/adapter/list.go.", AgentRole.Developer);
        var goal = UnknownGoal(task);

        Assert.Empty(GoalFileScopeInference.FromGoal(goal, 64_000, out var truncated));
        Assert.False(truncated);
        var scope = GoalFileScopeInference.ForScheduling(goal, task);
        Assert.Empty(scope.Includes);
        Assert.Equal(RepositoryScopeConfidence.Unknown, scope.Confidence);
        Assert.Equal(["refined repository scope is explicitly unknown"], scope.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Callers_TargetExistence_ControlsApplicabilityAndCollision(bool exists)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-neutral-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string path = "internal/adapter/list.go";
            const string text = "## Scope\ninternal/adapter/list.go";
            if (exists)
            {
                var fullPath = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, "package adapter");
            }

            Assert.Equal([path], GoalObjectivePlanner.Build(text).FileScopes);
            var plan = GoalObjectivePlanner.Build(text, repositoryRoot: root);
            Assert.Equal(exists ? new[] { path } : [], plan.FileScopes);
            Assert.Equal(!exists, plan.RiskLabels.Contains("scope-implicit"));

            var goal = new Goal(GoalId.New(), text,
                [new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer)]);
            var unfiltered = GoalScopeCollisionAdvisor.Build([text], [goal]);
            Assert.Equal(goal.Id.Value, Assert.Single(unfiltered.Collisions).GoalId);
            var filtered = GoalScopeCollisionAdvisor.Build([text], [goal], repositoryRoot: root);
            Assert.Equal(exists ? new[] { path } : [], filtered.ProposedScopes.Select(scope => scope.Path));
            Assert.Equal(exists ? 1 : 0, filtered.Collisions.Count);
            Assert.Equal(exists ? ScopeCollisionVerdict.OverlapDetected : ScopeCollisionVerdict.InsufficientEvidence,
                filtered.Verdict);

            const string newText = "## Scope\n- the new `packages/ui/widget.ts`;";
            Assert.Equal(["packages/ui/widget.ts"],
                GoalObjectivePlanner.Build(newText, repositoryRoot: root).FileScopes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static Goal UnknownGoal(TaskSpec task) => new(GoalId.New(), string.Join('\n',
        BacklogIntakePlanner.TargetScopeHeadingLine, BacklogIntakePlanner.UnknownScopeMarkerLine,
        BacklogIntakePlanner.ScopeIncludesHeadingLine, "- src/Generated/Guess.cs"), [task]);
}
