using System.Reflection;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: AsyncLocal console capture and immutable, local fixtures.
public sealed class FindingLookupTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoMatches_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            FindingLookupTextView.PrintFindingLookup(new("finding-1", [])));

        Assert.Equal("0 finding occurrence(s) match 'finding-1'." + Environment.NewLine, actual);
    }

    [Fact]
    public void Print_ReceiptsWithAndWithoutArms_PreservesExactText()
    {
        var request = new FindingEvidenceRequest([new("tests/Core.csproj", "BuildTests")]);
        var match = Match(request) with
        {
            ReviewedCommit = null,
            Receipts =
            [
                new("receipt-green", "candidate-sha", request, true, true, "Green summary",
                [
                    new(FindingEvidenceArm.Candidate, "candidate-sha",
                        FindingEvidenceArmDisposition.Green, true, true, "Passed"),
                    new(FindingEvidenceArm.Baseline, "baseline-sha",
                        FindingEvidenceArmDisposition.Red, true, false, "Failed")
                ]),
                new("receipt-red", "other-sha", request, false, false, "No arms")
            ]
        };

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            FindingLookupTextView.PrintFindingLookup(new("finding-1", [match])));

        Assert.Equal(string.Join(Environment.NewLine,
            "1 finding occurrence(s) match 'finding-1'.",
            "Goal goal-id, task 7 (task-id), role Reviewer",
            "  Reviewed commit: none; completed: 2026-10-09T12:34:56.0000000+02:00",
            "  finding-1: Blocking, Open, TestEvidence",
            "  Location: build.cs::Build",
            "  Description: Build evidence needed.",
            "  Receipt receipt-green: candidate candidate-sha, Accepted=True, Passed=True, Summary=Green summary",
            "    Arm Candidate: sha candidate-sha, Green, Accepted=True, Passed=True, Summary=Passed",
            "    Arm Baseline: sha baseline-sha, Red, Accepted=True, Passed=False, Summary=Failed",
            "  Receipt receipt-red: candidate other-sha, Accepted=False, Passed=False, Summary=No arms",
            ""), actual);
    }

    [Fact]
    public void Print_MissingReceiptReason_PreservesExactText()
    {
        var match = Match() with { MissingReceiptReason = "Receipt not available." };

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            FindingLookupTextView.PrintFindingLookup(new("finding-1", [match])));

        Assert.Equal(string.Join(Environment.NewLine,
            "1 finding occurrence(s) match 'finding-1'.",
            "Goal goal-id, task 7 (task-id), role Reviewer",
            "  Reviewed commit: reviewed-sha; completed: 2026-10-09T12:34:56.0000000+02:00",
            "  finding-1: Blocking, Open, TestEvidence",
            "  Location: build.cs::Build",
            "  Description: Build evidence needed.",
            "  Receipt not available.",
            ""), actual);
    }

    [Fact]
    public void Print_NoRequestOrReceipts_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() =>
            FindingLookupTextView.PrintFindingLookup(new("finding-1", [Match()])));

        Assert.Equal(string.Join(Environment.NewLine,
            "1 finding occurrence(s) match 'finding-1'.",
            "Goal goal-id, task 7 (task-id), role Reviewer",
            "  Reviewed commit: reviewed-sha; completed: 2026-10-09T12:34:56.0000000+02:00",
            "  finding-1: Blocking, Open, TestEvidence",
            "  Location: build.cs::Build",
            "  Description: Build evidence needed.",
            "  no evidence requested",
            ""), actual);
    }

    [Fact]
    public void Print_RequestWithoutReceiptOrReason_PreservesExactText()
    {
        var request = new FindingEvidenceRequest([new("tests/Core.csproj", "BuildTests")]);

        var actual = AsyncLocalConsoleRouter.Capture(() =>
            FindingLookupTextView.PrintFindingLookup(new("finding-1", [Match(request)])));

        Assert.Equal(string.Join(Environment.NewLine,
            "1 finding occurrence(s) match 'finding-1'.",
            "Goal goal-id, task 7 (task-id), role Reviewer",
            "  Reviewed commit: reviewed-sha; completed: 2026-10-09T12:34:56.0000000+02:00",
            "  finding-1: Blocking, Open, TestEvidence",
            "  Location: build.cs::Build",
            "  Description: Build evidence needed.",
            ""), actual);
    }

    [Fact]
    public void ExtractedOwner_LeavesNoPartialFileOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.FindingLookup.cs")));
        var source = File.ReadAllText(Path.Combine(cliRoot, "FindingLookupTextView.cs"));
        Assert.Contains("internal static class FindingLookupTextView", source);
        Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
        var methods = typeof(ConsoleViews).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method => method.Name == "PrintFindingLookup");
    }

    private static FindingLookupMatch Match(FindingEvidenceRequest? request = null) =>
        new(new GoalId("goal-id"), new TaskId("task-id"), 7, AgentRole.Reviewer, "reviewed-sha",
            new DateTimeOffset(2026, 10, 9, 12, 34, 56, TimeSpan.FromHours(2)),
            new ReviewFinding("finding-1", ReviewFindingState.Open, new("build.cs", "Build"),
                "Build evidence needed.", Category: FindingCategory.TestEvidence, EvidenceRequest: request), [], null);
}
