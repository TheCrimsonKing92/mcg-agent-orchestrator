using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class ChangedExistingTestsBriefSectionTests
{
    private const string Closing = "Each frozen-class violation is a blocking finding with stable id `frozen-fact-scope-<Type.Method>`. Check each `no ruling` entry against any criterion that keeps tests unmodified.";

    [Fact]
    public void ReviewerListsViolationsFirst()
    {
        var (kernel, goal) = CreateGoal();
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, null, "Bounded amendment", HumanWaitKind.SpecClarification).Request;
        kernel.SubmitHumanInput(request.Id, new FrozenFactRuling(["B"],
            [new("B.Allowed", "tests/B.cs", "Change the assertion")], "New contract", "Other facts", "Inspect diff", ["receipt"]).Render());
        ChangedExistingTest[] entries =
        [
            new("A", "NoRuling", "tests/A.cs", 2, 4, false),
            new("B", "Removed", "tests/B.cs", 8, 12, true, ChangedExistingTestClass.FrozenClassViolation, request.Id.Value),
            new("B", "Allowed", "tests/B.cs", 14, 18, false, ChangedExistingTestClass.AllowedByRuling, request.Id.Value)
        ];
        var source = Build(kernel, goal, AgentRole.Reviewer, entries);
        var section = Assert.Single(source.Segments.Where(segment => segment.Lines.Contains("## Changed existing tests")));
        Assert.Null(section.CollapsedLines);
        Assert.Null(section.TypedProjectionIdentity);
        Assert.Equal($"- B.Removed (tests/B.cs:8-12) removed: frozen-class violation {request.Id.Value}", section.Lines[1]);
        Assert.Contains("- A.NoRuling (tests/A.cs:2-4) changed: no ruling", section.Lines);
        Assert.Contains($"- B.Allowed (tests/B.cs:14-18) changed: allowed by ruling {request.Id.Value}", section.Lines);
        Assert.Equal(Closing, section.Lines[^2]);
        var brief = source.ProjectLegacyMarkedTextV1(false).Content;
        Assert.True(brief.IndexOf("## Changed existing tests", StringComparison.Ordinal) >
                    brief.IndexOf("## Frozen-fact rulings", StringComparison.Ordinal));
        foreach (var role in new[] { AgentRole.Developer, AgentRole.Tester })
            Assert.DoesNotContain("## Changed existing tests", Build(kernel, goal, role, entries).ProjectLegacyMarkedTextV1(false).Content);
    }

    [Fact]
    public void DegradedReviewerSectionContainsOnlyDiagnostic()
    {
        var (kernel, goal) = CreateGoal();
        const string diagnostic = "Changed existing tests unavailable: git show missing failed.";
        var source = Build(kernel, goal, AgentRole.Reviewer, [], diagnostic);
        var section = Assert.Single(source.Segments.Where(segment => segment.Lines.Contains("## Changed existing tests")));
        Assert.Equal(new[] { "## Changed existing tests", diagnostic, string.Empty }, section.Lines);
        Assert.DoesNotContain("## Changed existing tests", Build(kernel, goal, AgentRole.Developer, [], diagnostic).ProjectLegacyMarkedTextV1(false).Content);
        Assert.DoesNotContain("## Changed existing tests", Build(kernel, goal, AgentRole.Reviewer, []).ProjectLegacyMarkedTextV1(false).Content);
    }

    [Fact]
    public void CapShowsViolationsBeforeSixtyOtherEntriesAndCountsOmittedEntries()
    {
        var (kernel, goal) = CreateGoal();
        var entries = Enumerable.Range(1, 60).Select(index => new ChangedExistingTest("A", $"M{index}", "tests/A.cs", index, index, false)).ToList();
        entries.Add(new("Z", "Frozen", "tests/Z.cs", 90, 91, false, ChangedExistingTestClass.FrozenClassViolation, "request"));
        var section = Assert.Single(Build(kernel, goal, AgentRole.Reviewer, entries).Segments
            .Where(segment => segment.Lines.Contains("## Changed existing tests")));
        Assert.Equal("- Z.Frozen (tests/Z.cs:90-91) changed: frozen-class violation request", section.Lines[1]);
        Assert.Equal(60, section.Lines.Count(line => line.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Contains("1 more entries not shown.", section.Lines);
        Assert.Equal(Closing, section.Lines[^2]);
    }

    private static TaskBriefSource Build(AgentOrchestratorKernel kernel, Goal goal, AgentRole role,
        IReadOnlyList<ChangedExistingTest> entries, string? diagnostic = null) =>
        kernel.BuildTaskBriefSource(goal.Id, goal.Tasks.Single(task => task.RequiredRole == role).Id,
            changedExistingTests: entries, changedExistingTestsDiagnostic: diagnostic);

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Review existing fact scope",
            [new(TaskId.New(), "Review", AgentRole.Reviewer), new(TaskId.New(), "Implement", AgentRole.Developer),
                new(TaskId.New(), "Test", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (kernel, goal);
    }
}
