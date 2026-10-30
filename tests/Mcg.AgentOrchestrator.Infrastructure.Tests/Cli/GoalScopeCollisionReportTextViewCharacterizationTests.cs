using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: AsyncLocal console capture and local fixtures.
// Expected text follows ConsoleViews.GoalScopeCollision.cs at base 781c2fd21.
public sealed class GoalScopeCollisionReportTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoCollisionsOrSubject_PreservesExactText()
    {
        var report = new GoalScopeCollisionReport(
            [], 0, 0, 0, 0, 0, ScopeCollisionVerdict.NoOverlapDetected, [], []);

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalScopeCollisionReportTextView.Print(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "Scope collision advisory: no-overlap-detected conflicts=0 inputGoals=0 eligibleGoals=0 comparedGoals=0 uncheckableGoals=0 uncomparedGoals=0 explicitConflicts=0",
            """{"intakeItemId":null,"heading":null,"verdict":"no-overlap-detected","inputGoalCount":0,"eligibleGoalCount":0,"comparedGoalCount":0,"uncheckableGoalCount":0,"uncomparedGoalCount":0,"explicitCollisionCount":0,"conflictingGoalIds":[],"collisions":[],"evidenceGaps":[],"proposedScopes":[]}""",
            ""), actual);
    }

    [Fact]
    public void Print_CollisionsAndEvidenceGap_PreservesExactText()
    {
        var report = new GoalScopeCollisionReport(
            [new("src/View.cs", FileScopeProvenance.Explicit)], 4, 3, 1, 1, 2,
            ScopeCollisionVerdict.OverlapDetectedIncomplete,
            [
                new("bbbbbbbb00000000", "bbbbbbbb", "Active", ScopeCollisionKind.ExactFile,
                    "src/View.cs", FileScopeProvenance.Explicit, "src/View.cs", FileScopeProvenance.Derived),
                new("aaaaaaaa00000000", "aaaaaaaa", "Active", ScopeCollisionKind.DirectoryPrefix,
                    "src/View.cs", FileScopeProvenance.Explicit, "src", FileScopeProvenance.Inferred)
            ],
            [new(null, ScopeEvidenceGap.ComparisonTruncated, "Comparison limited")]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            GoalScopeCollisionReportTextView.Print(report, "item-1", "Update CLI"));

        Assert.Equal(string.Join(Environment.NewLine,
            "Scope collision advisory: item=item-1 heading=\"Update CLI\" overlap-detected-incomplete conflicts=2 inputGoals=4 eligibleGoals=3 comparedGoals=1 uncheckableGoals=1 uncomparedGoals=2 explicitConflicts=1",
            """{"intakeItemId":"item-1","heading":"Update CLI","verdict":"overlap-detected-incomplete","inputGoalCount":4,"eligibleGoalCount":3,"comparedGoalCount":1,"uncheckableGoalCount":1,"uncomparedGoalCount":2,"explicitCollisionCount":1,"conflictingGoalIds":["aaaaaaaa00000000","bbbbbbbb00000000"],"collisions":[{"goalId":"bbbbbbbb00000000","goalPrefix":"bbbbbbbb","goalStatus":"Active","kind":"ExactFile","proposedPath":"src/View.cs","proposedProvenance":"Explicit","conflictingPath":"src/View.cs","conflictingProvenance":"Derived"},{"goalId":"aaaaaaaa00000000","goalPrefix":"aaaaaaaa","goalStatus":"Active","kind":"DirectoryPrefix","proposedPath":"src/View.cs","proposedProvenance":"Explicit","conflictingPath":"src","conflictingProvenance":"Inferred"}],"evidenceGaps":[{"goalId":null,"gap":"ComparisonTruncated","message":"Comparison limited"}],"proposedScopes":[{"path":"src/View.cs","provenance":"Explicit"}]}""",
            ""), actual);
    }
}
