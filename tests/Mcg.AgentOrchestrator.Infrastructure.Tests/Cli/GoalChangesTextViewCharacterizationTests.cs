using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: AsyncLocal console capture and local fixtures.
// Every expected string is derived from ConsoleViews.GoalChanges.cs at base fd85596fc,
// including its default System.Text.Json serialization (numeric enums and platform newlines).
public sealed class GoalChangesTextViewCharacterizationTests
{
    [Fact]
    public void Print_NoEntries_PreservesExactText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalChangesTextView.Print(EmptyReport()));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "goal-changes aaaaaaaa (Active)",
            "  Extract changes",
            "",
            "  No file changes recorded for this goal.",
            "", ""), actual);
    }

    [Fact]
    public void Print_MixedAttributionsAndRoles_PreservesExactText()
    {
        var report = EmptyReport() with
        {
            GoalObjective = new string('o', 81),
            Entries =
            [new("tester-task", "Check output", AgentRole.Tester, GoalChangesAttribution.Approximate,
                ["tests/Check.cs"], null, null),
             new("dev-task", new string('d', 73), AgentRole.Developer, GoalChangesAttribution.Exact,
                ["src/B.cs", "src/A.cs"], "1234567890", "abcdef0123"),
             new("research-task", "Survey sources", AgentRole.Researcher, GoalChangesAttribution.Uncommitted,
                [], null, null),
             new("short", "Small commit", AgentRole.Developer, GoalChangesAttribution.Exact,
                [], null, "abc")]
        };

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalChangesTextView.Print(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "",
            "goal-changes aaaaaaaa (Active)",
            "  " + new string('o', 80) + "...",
            "",
            "== Researcher ==",
            "  task research (uncommitted)",
            "    Survey sources",
            "    (no files changed)",
            "",
            "== Developer ==",
            "  task dev-task 12345678..abcdef01",
            "    " + new string('d', 72) + "...",
            "    src/B.cs",
            "    src/A.cs",
            "",
            "  task short ?..abc",
            "    Small commit",
            "    (no files changed)",
            "",
            "== Tester ==",
            "  task tester-t (approximate — legacy dispatch)",
            "    Check output",
            "    tests/Check.cs",
            "", ""), actual);
    }

    [Fact]
    public void PrintFlat_NoEntries_EmitsNothing()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalChangesTextView.PrintFlat(EmptyReport()));

        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void PrintFlat_DuplicateAndUnsortedFiles_PreservesExactText()
    {
        var report = EmptyReport() with
        {
            Entries =
            [new("one", "First", AgentRole.Developer, GoalChangesAttribution.Exact,
                ["src/Z.cs", "src/a.cs"], null, null),
             new("two", "Second", AgentRole.Tester, GoalChangesAttribution.Uncommitted,
                ["SRC/A.CS", "README.md", "src/b.cs", "src/Z.cs"], null, null)]
        };

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalChangesTextView.PrintFlat(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "README.md", "src/a.cs", "src/b.cs", "src/Z.cs", ""), actual);
    }

    [Fact]
    public void PrintJson_NoEntries_PreservesExactJsonText()
    {
        var actual = AsyncLocalConsoleRouter.Capture(() => GoalChangesTextView.PrintJson(EmptyReport()));

        Assert.Equal(string.Join(Environment.NewLine,
            "{",
            "  \"GoalId\": \"aaaaaaaa00000000\",",
            "  \"GoalPrefix\": \"aaaaaaaa\",",
            "  \"GoalObjective\": \"Extract changes\",",
            "  \"GoalStatus\": \"Active\",",
            "  \"Entries\": []",
            "}") + Environment.NewLine, actual);
    }

    [Fact]
    public void PrintJson_EntryWithEscapedTextAndNullHash_PreservesExactJsonText()
    {
        var report = EmptyReport() with
        {
            GoalObjective = "Extract <changes>",
            Entries = [new("dev-task", "Move output", AgentRole.Developer,
                GoalChangesAttribution.Uncommitted, ["src/View.cs"], null, "abc")]
        };

        var actual = AsyncLocalConsoleRouter.Capture(() => GoalChangesTextView.PrintJson(report));

        Assert.Equal(string.Join(Environment.NewLine,
            "{",
            "  \"GoalId\": \"aaaaaaaa00000000\",",
            "  \"GoalPrefix\": \"aaaaaaaa\",",
            "  \"GoalObjective\": \"Extract \\u003Cchanges\\u003E\",",
            "  \"GoalStatus\": \"Active\",",
            "  \"Entries\": [",
            "    {",
            "      \"TaskId\": \"dev-task\",",
            "      \"TaskDescription\": \"Move output\",",
            "      \"Role\": 3,",
            "      \"Attribution\": 1,",
            "      \"Files\": [",
            "        \"src/View.cs\"",
            "      ],",
            "      \"BaseCommit\": null,",
            "      \"ResultCommit\": \"abc\"",
            "    }",
            "  ]",
            "}") + Environment.NewLine, actual);
    }

    private static GoalChangesReport EmptyReport() =>
        new("aaaaaaaa00000000", "aaaaaaaa", "Extract changes", "Active", []);
}
