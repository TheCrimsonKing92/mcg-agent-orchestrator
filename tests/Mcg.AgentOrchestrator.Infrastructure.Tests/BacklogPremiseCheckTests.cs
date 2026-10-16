using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all fixtures are in memory and supply their own clock and path predicate.
public sealed class BacklogPremiseCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private const string ItemId = "11111111aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DoneId = "22222222bbbbbbbbbbbbbbbbbbbbbbbb";
    private const string GoalId = "33333333cccccccccccccccccccccccc";

    [Fact]
    public void MissingPathsAndFinishedReferencePropose_ButOneExistingPathDoesNot()
    {
        var item = Item() with { Body = "`src/Removed.cs:12` and (docs/removed.md)." };
        var done = Item(DoneId, "Finished unrelated work") with { Status = BacklogItemStatus.Done, Body = "Replaces 11111111." };
        var proposal = Assert.Single(Evaluate([item, done]).Proposals);
        Assert.Equal("A,B", proposal.Rules);
        Assert.Equal("missing:docs/removed.md|src/Removed.cs;refs:22222222", proposal.Evidence);

        var control = Evaluate([item, done], exists: path => path == "src/Removed.cs");
        Assert.Empty(control.Proposals);
        Assert.Equal("some-paths-missing", Assert.Single(control.Skim).Group);
        Assert.Empty(Evaluate([item]).Proposals);
        Assert.Equal("some-paths-missing", Assert.Single(Evaluate([item]).Skim).Group);
    }

    [Theory]
    [InlineData(GoalStatus.Completed, SourceBacklogCoverage.Full, true)]
    [InlineData(GoalStatus.Completed, null, true)]
    [InlineData(GoalStatus.Completed, SourceBacklogCoverage.Slice, false)]
    [InlineData(GoalStatus.Active, SourceBacklogCoverage.Full, false)]
    public void LinkedGoalRequiresCompletionAndNonSliceCoverage(GoalStatus status, SourceBacklogCoverage? coverage, bool proposes)
    {
        var result = Evaluate([Item()], [new(GoalId, status, "Different objective", ItemId, coverage)]);
        Assert.Equal(proposes ? 1 : 0, result.Proposals.Count);
        if (proposes)
        {
            Assert.Equal("C", result.Proposals[0].Rules);
            Assert.Equal("goal:33333333", result.Proposals[0].Evidence);
        }
    }

    [Theory]
    [InlineData("Fix the thing (backlog 11111111).", true)]
    [InlineData("Fix the thing (backlog item `11111111).", true)]
    [InlineData("Fix the thing (backlog 11111111aaaaaaaaaaaaaaaaaaaaaaaa).", true)]
    [InlineData("Fix department (backlog 11111111).", true)]
    [InlineData("Fix the thing (backlog 11111111, step 1).", false)]
    [InlineData("Related: 11111111 and 44444444", false)]
    [InlineData("Fix the thing\n(backlog 11111111).", false)]
    [InlineData("Fix the thing (backlog 11111111 and 44444444).", false)]
    [InlineData("Consolidating changes (backlog 11111111).", false)]
    [InlineData("Fix residual work (backlog 11111111).", false)]
    [InlineData("Fix FOLLOW-UP (backlog 11111111).", false)]
    [InlineData("Fix first phase (backlog 11111111).", false)]
    public void SoleTargetRejectsPartialOrMultipleTargets(string objective, bool proposes)
    {
        var result = Evaluate([Item()], [new(GoalId, GoalStatus.Completed, objective)]);
        Assert.Equal(proposes ? 1 : 0, result.Proposals.Count);
        if (proposes)
        {
            Assert.Contains("D", result.Proposals[0].Rules);
            Assert.Contains("goal:33333333", result.Proposals[0].Evidence);
        }
    }

    [Fact]
    public void EligibilityUsesStrictAgeAndExemptsNorthStarAndEpicsEvenWithFinishedGoals()
    {
        var boundary = Item() with { UpdatedAt = Now.AddDays(-30) };
        Assert.Equal(0, Evaluate([boundary]).Eligible);
        Assert.Equal(1, Evaluate([boundary with { UpdatedAt = Now.AddDays(-31) }]).Eligible);
        var northStar = Item(title: "  north star: long horizon");
        var epicItem = Item(DoneId);
        var doneItem = Item(GoalId) with { Status = BacklogItemStatus.Done };
        var result = BacklogPremiseCheck.Evaluate([northStar, epicItem, doneItem],
            [new(GoalId, GoalStatus.Completed, "Unrelated", northStar.Id, SourceBacklogCoverage.Full),
             new(DoneId, GoalStatus.Completed, "Unrelated", epicItem.Id, SourceBacklogCoverage.Full)],
            new HashSet<string> { epicItem.Id }, _ => false, Now);
        Assert.Equal(0, result.Eligible);
        Assert.Equal(1, result.ExemptNorthStar);
        Assert.Equal(1, result.ExemptEpic);
        Assert.Empty(result.Proposals);
        Assert.Empty(result.Skim);
    }

    [Fact]
    public void CitedPathsExcludeOutputsGlobsAndTraversalAndStripLineSuffixesIncludingNotes()
    {
        var excluded = new[] { "obj", "bin", "TestResults", "artifacts", ".scratch", ".." }
            .Select(segment => $"src/Project/{segment}/removed.cs").ToArray();
        var glob = string.Join('/', "tests", "cases", "*.cs");
        var calls = new List<string>();
        var item = Item() with
        {
            Body = string.Join(' ', excluded.Append(glob).Append("xsrc/not-a-path.cs").Append("/src/not-cited.cs")),
            Notes = [new(Now.AddDays(-40), "[config/old.json:42] 'docs/old.md', scripts/retired.ps1.")]
        };
        var done = Item(DoneId, "Completed cleanup") with { Status = BacklogItemStatus.Done, Body = "See 11111111" };
        var result = Evaluate([item, done], exists: path => { calls.Add(path); return false; });
        Assert.Equal(["config/old.json", "docs/old.md", "scripts/retired.ps1"], calls);
        Assert.Equal("A,B", Assert.Single(result.Proposals).Rules);
        var onlyExcluded = Evaluate([item with { Notes = [] }], exists: _ => throw new InvalidOperationException("Excluded path checked"));
        Assert.Empty(onlyExcluded.Proposals);
        Assert.Equal("no-signal", Assert.Single(onlyExcluded.Skim).Group);
    }

    [Theory]
    [InlineData("alpha bravo charlie delta", true)]
    [InlineData("alpha bravo charlie", false)]
    [InlineData("an is of", false)]
    public void TitleOverlapUsesJaccardThreshold(string doneTitle, bool referenced)
    {
        var item = Item(title: "Alpha bravo charlie delta echo");
        var done = Item(DoneId, doneTitle) with { Status = BacklogItemStatus.Done };
        var result = Evaluate([item, done]);
        Assert.Empty(result.Proposals);
        Assert.Equal(referenced ? "referenced" : "no-signal", Assert.Single(result.Skim).Group);
    }

    [Theory]
    [InlineData("(11111111)", true)]
    [InlineData("a11111111", false)]
    [InlineData("11111111a", false)]
    public void ReferenceRequiresHexBoundaries(string reference, bool referenced)
    {
        var item = Item() with { Body = "src/removed.cs" };
        var done = Item(DoneId, "Other") with { Status = BacklogItemStatus.Done, Body = reference };
        Assert.Equal(referenced ? 1 : 0, Evaluate([item, done]).Proposals.Count);
    }

    [Fact]
    public void CompletedGoalObjectiveCanReferenceByIdOrFirstLineTitle_ActiveGoalCannot()
    {
        var item = Item(title: "Alpha bravo charlie delta echo") with { Body = "src/removed.cs" };
        var goal = new BacklogPremiseCheck.GoalInput(GoalId, GoalStatus.Completed, "Unrelated\nRelated: 11111111");
        Assert.Equal("missing:src/removed.cs;refs:33333333", Assert.Single(Evaluate([item], [goal]).Proposals).Evidence);
        Assert.Single(Evaluate([item], [goal with { Objective = "Alpha bravo charlie delta" }]).Proposals);
        Assert.Empty(Evaluate([item], [goal with { Objective = "Unrelated\nAlpha bravo charlie delta" }]).Proposals);
        Assert.Empty(Evaluate([item], [goal with { Status = GoalStatus.Active }]).Proposals);
    }

    [Fact]
    public void SkimGroupsAreOrderedBySignalThenOldestWithIdTieBreak()
    {
        var missingNew = Item("00000001aaaaaaaaaaaaaaaaaaaaaaaa", "amber") with { Body = "src/gone.cs", UpdatedAt = Now.AddDays(-40) };
        var missingOld = Item("00000002aaaaaaaaaaaaaaaaaaaaaaaa", "bronze") with { Body = "docs/gone.md", UpdatedAt = Now.AddDays(-90) };
        var referencedNew = Item("00000003aaaaaaaaaaaaaaaaaaaaaaaa", "copper") with { UpdatedAt = Now.AddDays(-45) };
        var referencedOld = Item("00000004aaaaaaaaaaaaaaaaaaaaaaaa", "diamond") with { UpdatedAt = Now.AddDays(-80) };
        var plainNew = Item("00000005aaaaaaaaaaaaaaaaaaaaaaaa", "emerald") with { UpdatedAt = Now.AddDays(-50) };
        var plainOld = Item("00000006aaaaaaaaaaaaaaaaaaaaaaaa", "fuchsia") with { UpdatedAt = Now.AddDays(-70) };
        var done = Item(DoneId, "Finished") with { Status = BacklogItemStatus.Done, Body = "00000003 and 00000004" };
        var result = Evaluate([plainNew, referencedNew, missingNew, done, plainOld, referencedOld, missingOld]);
        Assert.Equal(new[] { missingOld.Id, missingNew.Id, referencedOld.Id, referencedNew.Id, plainOld.Id, plainNew.Id }, result.Skim.Select(entry => entry.Item.Id));
        Assert.Equal(["some-paths-missing", "some-paths-missing", "referenced", "referenced", "no-signal", "no-signal"], result.Skim.Select(entry => entry.Group));
        var ties = Evaluate([plainOld with { UpdatedAt = plainNew.UpdatedAt }, plainNew]);
        Assert.Equal(new[] { plainNew.Id, plainOld.Id }, ties.Skim.Select(entry => entry.Item.Id));
    }

    private static BacklogItem Item(string id = ItemId, string title = "Stale widget") =>
        new(id, title, "", BacklogItemStatus.Open, Now.AddDays(-100), Now.AddDays(-60), null);

    private static BacklogPremiseCheck.Result Evaluate(IReadOnlyList<BacklogItem> items,
        IReadOnlyList<BacklogPremiseCheck.GoalInput>? goals = null, Func<string, bool>? exists = null) =>
        BacklogPremiseCheck.Evaluate(items, goals ?? [], new HashSet<string>(), exists ?? (_ => false), Now);
}
