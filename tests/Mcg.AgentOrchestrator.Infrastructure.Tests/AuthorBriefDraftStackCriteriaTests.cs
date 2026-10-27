using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure text checks and a private fixture root/SQLite store; model and Git are fakes.
public sealed class AuthorBriefDraftStackCriteriaTests
{
    private const string HouseCriterion = "The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string GoCriterion = "The acceptance manifest checks pass: go build -buildvcs=false ./..., go vet ./... and go test -count=1 ./.... Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string NeutralCriterion = "The acceptance manifest checks pass using the project's own build and test commands. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string GoNote = "Workers run go build, go vet and go test themselves and never report test classes.";
    private const string NeutralNote = "Workers follow the target repository’s build and test instructions and report evidence using that stack’s conventions.";

    public static IEnumerable<object?[]> Stacks() =>
        new Toolchain?[] { null }.Concat(Enum.GetValues<Toolchain>().Select(stack => (Toolchain?)stack))
            .Select(stack => new object?[] { stack });

    [Theory]
    [InlineData(null)]
    [InlineData(Toolchain.Dotnet)]
    public void Render_HouseStack_PreservesVerbatimCriterion(Toolchain? stack)
    {
        var prompt = Render(stack).ReplaceLineEndings("\n");

        Assert.Contains("The last two acceptance criteria are the following house Developer deferred-tests criterion, copied character for character, followed by one single-sentence Reviewer criterion:\n" +
            HouseCriterion + "\nKeep What to build", prompt);
        Assert.DoesNotContain("The acceptance manifest checks pass", prompt);
        Assert.DoesNotContain(GoNote, prompt);
        Assert.DoesNotContain(NeutralNote, prompt);
    }

    [Fact]
    public void Render_GoStack_UsesManifestAndWorkerCommands()
    {
        var prompt = Render(Toolchain.Go);

        Assert.Contains(GoCriterion, prompt);
        Assert.Contains(GoNote, prompt);
        Assert.DoesNotContain("tests: deferred - ", prompt);
        Assert.DoesNotContain("every test class it touched or added", prompt);
        Assert.DoesNotContain(NeutralCriterion, prompt);
    }

    [Theory]
    [InlineData(Toolchain.Node)]
    [InlineData(Toolchain.Python)]
    [InlineData(Toolchain.Unknown)]
    public void Render_OtherStack_UsesNeutralGuidance(Toolchain stack)
    {
        var prompt = Render(stack);

        Assert.Contains(NeutralCriterion, prompt);
        Assert.Contains(NeutralNote, prompt);
        Assert.DoesNotContain("tests: deferred - ", prompt);
        Assert.DoesNotContain("every test class it touched or added", prompt);
        Assert.DoesNotContain(GoNote, prompt);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public void Render_EveryStack_IncludesStandingSentences(Toolchain? stack)
    {
        var lines = Render(stack).ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("A step observed by a person after the goal lands, including a check on a particular operating system, goes in prose outside the numbered criteria, and no criterion is owned by the Operator for such a step.", lines);
        Assert.Contains("Every criterion asks for evidence only from files in the target repository, so a Reviewer criterion never asks for a comparison against source outside the worktree.", lines);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public void Run_EveryStack_RequiresItsStandingCriterion(Toolchain? stack)
    {
        var expected = stack is null or Toolchain.Dotnet ? HouseCriterion :
            stack == Toolchain.Go ? GoCriterion : NeutralCriterion;
        foreach (var criterion in new[] { HouseCriterion, GoCriterion, NeutralCriterion })
        {
            var check = Check(criterion, stack);
            Assert.Equal(criterion == expected, check.Passed);
            if (!check.Passed && stack is not (null or Toolchain.Dotnet))
                Assert.Contains(expected, check.Detail);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Toolchain.Dotnet)]
    public void Run_HouseStack_PreservesPredicateAndDetails(Toolchain? stack)
    {
        var present = Check("Any text containing tests: deferred - ExampleTests. Developer owns; Acceptance executes. TEST-VERIFIABLE.   ", stack);
        Assert.True(present.Passed, present.Detail);
        Assert.Equal("Developer deferred-tests criterion present.", present.Detail);

        var absent = Check(GoCriterion, stack);
        Assert.False(absent.Passed);
        Assert.Equal("Observed 1 declared criteria; none contains tests: deferred - and ends with Developer owns; Acceptance executes. TEST-VERIFIABLE.", absent.Detail);
    }

    [Theory]
    [InlineData(Toolchain.Go, GoCriterion)]
    [InlineData(Toolchain.Node, NeutralCriterion)]
    [InlineData(Toolchain.Python, NeutralCriterion)]
    [InlineData(Toolchain.Unknown, NeutralCriterion)]
    public void Run_ManifestStack_TrimsAndIgnoresCaseButRejectsExtraText(
        Toolchain stack, string criterion)
    {
        Assert.True(Check("  " + criterion.ToUpperInvariant() + "  ", stack).Passed);
        Assert.False(Check(criterion + " Extra text.", stack).Passed);
        Assert.False(Check("Extra text. " + criterion, stack).Passed);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public void Run_EveryStack_AllowsStandingCriterionBeforeAnotherCriterion(Toolchain? stack)
    {
        var criterion = stack is null or Toolchain.Dotnet ? HouseCriterion :
            stack == Toolchain.Go ? GoCriterion : NeutralCriterion;
        var check = Check(criterion + "\n2. Scope reviewed. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.", stack);

        Assert.True(check.Passed, check.Detail);
    }

    [Theory]
    [MemberData(nameof(Stacks))]
    public void Run_NoCriteria_PreservesAbsenceDetail(Toolchain? stack)
    {
        var checks = AuthorBriefDraftChecks.Run("## Acceptance criteria\nNo criteria.",
            CliAuthorDraftCommandTests.Fixture.MainSha, new CliAuthorDraftCommandTests.FakeRepository(), stack);

        Assert.False(Assert.Single(checks, check => check.Name == "criteria-present").Passed);
        var standing = Assert.Single(checks, check => check.Name == "developer-deferred-criterion");
        Assert.True(standing.Passed);
        Assert.Equal("No declared criteria; criteria-present reports the absence.", standing.Detail);
    }

    [Fact]
    public void Service_GoWorkspace_UsesOneStackSnapshotForPromptAndChecks()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var root = fixture.Workspace.ExecutionDirectory;
        var solution = Path.Combine(root, "App.sln");
        var module = Path.Combine(root, "go.mod");
        File.Delete(solution);
        File.WriteAllText(module, "module example.test/draft\n");
        Assert.Equal(Toolchain.Go, GoalIntakeTargetStack.For(fixture.Workspace));
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace(HouseCriterion, GoCriterion);
        WorkerProcessRunRequest? captured = null;
        var seams = new AuthorBriefDraftSeams((request, _) =>
        {
            Assert.Null(captured);
            captured = request;
            // A changed marker during the round must not change the validation stack snapshot.
            File.Delete(module);
            File.WriteAllText(solution, "");
            return Task.FromResult(new WorkerProcessRunResult(0,
                JsonSerializer.Serialize(new { kind = "draft", markdown }), ""));
        }, fixture.Repository);

        var result = AuthorBriefDraftService.Run(fixture.Item.Id[..8], fixture.Workspace, seams,
            fixture.Output, fixture.Error);

        var prompt = Assert.IsType<WorkerProcessRunRequest>(captured).StandardInput!;
        Assert.Contains(GoCriterion, prompt);
        Assert.Contains(GoNote, prompt);
        Assert.DoesNotContain("tests: deferred - ", prompt);
        Assert.Equal(Toolchain.Dotnet, GoalIntakeTargetStack.For(fixture.Workspace));
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.Checks);
        Assert.All(result.Checks, check => Assert.True(check.Passed, check.Detail));
        Assert.True(Assert.Single(result.Checks, check => check.Name == "developer-deferred-criterion").Passed);
        Assert.Empty(fixture.Error.ToString());
    }

    private static string Render(Toolchain? stack) => AuthorBriefDraftPrompt.Render(
        new BacklogItem("item", "Draft title", "Draft body", BacklogItemStatus.Open,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null),
        ConductorLessonSelection.Empty, CliAuthorDraftCommandTests.Fixture.MainSha, stack);

    private static AuthorBriefDraftCheck Check(string criterion, Toolchain? stack) => Assert.Single(
        AuthorBriefDraftChecks.Run("## Acceptance criteria\n1. " + criterion + "\n## Scope\nDrafting only.",
            CliAuthorDraftCommandTests.Fixture.MainSha, new CliAuthorDraftCommandTests.FakeRepository(), stack),
        check => check.Name == "developer-deferred-criterion");
}
