using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliLessonsCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOrAbsentStore_ListsEmptyTextAndJsonWithoutCreatingOrWriting(bool setup)
    {
        using var fixture = new OperatorLessonHarness();
        var path = fixture.Workspace.OperatorLessonsStorePath;
        Assert.False(File.Exists(path));
        if (setup) SqliteOperatorLessonStore.Setup(path);
        var bytes = setup ? File.ReadAllBytes(path) : null;
        var modified = setup ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        using var text = new StringWriter();
        using var json = new StringWriter();

        Assert.Equal(0, CliLessonCommands.Run(["lessons"], fixture.Workspace, text));
        Assert.Equal($"No lessons recorded.{Environment.NewLine}", text.ToString());
        Assert.Equal(0, CliLessonCommands.Run(["lessons", "--json"], fixture.Workspace, json));
        Assert.Equal($"[]{Environment.NewLine}", json.ToString());
        Assert.Equal(setup, File.Exists(path));
        if (setup)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task RecordCommandQueuesTypedWorkspaceIntentWithoutWritingLesson()
    {
        using var fixture = new OperatorLessonHarness();
        var proof = Path.Combine(fixture.Root, "proof.txt");
        File.WriteAllText(proof, "proof");
        using var output = new StringWriter();

        Assert.Equal(0, CliLessonCommands.Run(["lesson", "record", "--situation", "When a run fails",
            "--rule", "Read its receipt", "--evidence", "operator-evidence:" + proof,
            "--applies-to", "tests", "--actor-kind", "agent"], fixture.Workspace, output));

        Assert.False(File.Exists(fixture.Workspace.OperatorLessonsStorePath));
        var queued = Assert.Single(await fixture.IntentStore.ListForGoalAsync(OperatorIntentScopes.Workspace));
        Assert.Equal(OperatorIntentStatus.Pending, queued.Status);
        Assert.Equal(OperatorIntentVerbs.LessonRecord, queued.Verb);
        Assert.Equal(OperatorActorKind.Agent, queued.ActorKind);
        Assert.Contains(queued.Id, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsActiveAndRetiredLessonsWithTagFilterInTextAndJson()
    {
        using var fixture = new OperatorLessonHarness();
        File.WriteAllText(Path.Combine(fixture.Root, "proof.txt"), "proof");
        var active = await fixture.Record(["operator-evidence:proof.txt"], ["dispatch"]);
        var retired = await fixture.Record(["operator-evidence:proof.txt"], ["build"]);
        fixture.Tick();
        await fixture.Retire(retired.Id, "Outdated rule");
        fixture.Tick();

        using var text = new StringWriter();
        Assert.Equal(0, CliLessonCommands.Run(["lessons", "--applies-to", "dispatch"], fixture.Workspace, text));
        Assert.Contains(active.Id, text.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(retired.Id, text.ToString(), StringComparison.Ordinal);

        using var allText = new StringWriter();
        Assert.Equal(0, CliLessonCommands.Run(["lessons", "--all"], fixture.Workspace, allText));
        Assert.Contains($"{retired.Id} | retired", allText.ToString(), StringComparison.Ordinal);
        Assert.Contains("reason=Outdated rule", allText.ToString(), StringComparison.Ordinal);

        using var json = new StringWriter();
        Assert.Equal(0, CliLessonCommands.Run(["lessons", "--all", "--json"], fixture.Workspace, json));
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Contains(document.RootElement.EnumerateArray(), item =>
            item.GetProperty("id").GetString() == retired.Id &&
            item.GetProperty("retireReason").GetString() == "Outdated rule");
    }
}
