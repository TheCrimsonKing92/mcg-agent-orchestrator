using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: explicit working directories and unique fixture stores; no ambient mutation.
public sealed class CliLessonsReviewDueTests
{
    [Fact]
    public async Task ChangedFile_AppendsMarkerAndReferenceWithoutFilteringLesson()
    {
        using var fixture = new OperatorLessonHarness();
        File.WriteAllText(Path.Combine(fixture.Root, "proof.txt"), "recorded\n");
        await fixture.Record(["operator-evidence:proof.txt:1"], ["steward"]);
        fixture.Tick();
        File.WriteAllText(Path.Combine(fixture.Root, "proof.txt"), "changed\n");

        var text = List(fixture);

        Assert.Contains(" review-due operator-evidence:proof.txt:1", text, StringComparison.Ordinal);
        Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);
        Assert.Single(new ConductorLessonSelector(fixture.Workspace.OperatorLessonsStorePath)
            .Select(["steward"]).Lessons);
    }

    [Fact]
    public async Task UnchangedFile_LeavesActiveLineIdentical()
    {
        using var fixture = new OperatorLessonHarness();
        File.WriteAllText(Path.Combine(fixture.Root, "proof.txt"), "one\r\ntwo\r\n");
        await fixture.Record(["operator-evidence:proof.txt:2"]);
        fixture.Tick();
        var lesson = Assert.Single(fixture.LessonStore.List());

        Assert.Equal($"{lesson.Id} | active | {lesson.Rule}{Environment.NewLine}", List(fixture));
        Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);
    }

    [Fact]
    public async Task DeletedFile_AppendsMissingMarkerAndReference()
    {
        using var fixture = new OperatorLessonHarness();
        var path = Path.Combine(fixture.Root, "proof.txt");
        File.WriteAllText(path, "proof\n");
        await fixture.Record(["operator-evidence:proof.txt"]);
        fixture.Tick();
        File.Delete(path);

        Assert.Contains(" review-due evidence-missing operator-evidence:proof.txt", List(fixture),
            StringComparison.Ordinal);
        Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);
    }

    [Fact]
    public async Task SeveralChangedReferences_ListsOneMarkerInRecordedOrder()
    {
        using var fixture = new OperatorLessonHarness();
        var first = Path.Combine(fixture.Root, "first.txt");
        var second = Path.Combine(fixture.Root, "second.txt");
        File.WriteAllText(first, "proof\n");
        File.WriteAllText(second, "proof\n");
        await fixture.Record(["operator-evidence:first.txt", "operator-evidence:second.txt"]);
        fixture.Tick();
        File.WriteAllText(first, "changed\n");
        File.Delete(second);

        Assert.EndsWith(" review-due operator-evidence:first.txt evidence-missing " +
            "operator-evidence:second.txt" + Environment.NewLine, List(fixture));
    }

    [Fact]
    public async Task JsonAndRetiredLines_DoNotCarryAdvisoryMarker()
    {
        using var fixture = new OperatorLessonHarness();
        var path = Path.Combine(fixture.Root, "proof.txt");
        File.WriteAllText(path, "proof\n");
        var intent = await fixture.Record(["operator-evidence:proof.txt"]);
        fixture.Tick();
        File.Delete(path);
        using var json = new StringWriter();
        Assert.Equal(0, CliLessonCommands.Run(["lessons", "--json"], fixture.Workspace, json, fixture.Root));
        Assert.DoesNotContain("review-due", json.ToString(), StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(json.ToString());
        Assert.Equal(intent.Id, parsed.RootElement[0].GetProperty("id").GetString());
        await fixture.Retire(intent.Id);
        fixture.Tick();
        using var text = new StringWriter();
        Assert.Equal(0, CliLessonCommands.Run(["lessons", "--all"], fixture.Workspace, text, fixture.Root));
        Assert.Contains(" | retired | ", text.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("review-due", text.ToString(), StringComparison.Ordinal);
    }

    private static string List(OperatorLessonHarness fixture)
    {
        using var output = new StringWriter();
        Assert.Equal(0, CliLessonCommands.Run(["lessons"], fixture.Workspace, output, fixture.Root));
        return output.ToString();
    }
}
