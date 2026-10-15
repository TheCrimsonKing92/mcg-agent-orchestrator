using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

// Parallel-safe: pure text cases and an isolated resolution scene with no initialized terminal.
public sealed class OwnerQuestionResolutionFieldLayoutTests
{
    private const string Needed = "Candidate-bound execution of the requested regression classes, supporting criteria 1-6.";
    private const string Reason = "The candidate and its test execution evidence do not exist yet; workers must defer test-host execution.";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData("post-implementation", "post-implementation; produced after the candidate exists")]
    [InlineData("retrievable", "retrievable from store 'operator-records', which the worker cannot reach")]
    [InlineData("never-recorded", "never-recorded; the evidence does not exist in an available record")]
    public void ProducerFieldsAndCompactSummaryHaveSeparateLabels(string availability, string expectedAvailability)
    {
        var resolution = Resolution(Question(availability));
        var text = OwnerQuestionResolutionText.Build(resolution, TimeZoneInfo.Utc);
        var lines = text.Split('\n');
        string[] expected = [
            "Request: Planner evidence request for criterion 1",
            "Evidence needed: " + Needed,
            "Availability: " + expectedAvailability,
            .. availability == "post-implementation" ? new[] { "Evidence owner: acceptance-gate" } : Array.Empty<string>(),
            "Reason: " + Reason,
            "Resolution: Answered by the operator at 00:00:00"];
        Assert.Equal(expected, lines.Skip(2).Take(expected.Length));
        foreach (var line in expected) Assert.Single(lines, value => value == line);
        Assert.DoesNotContain("Question:", lines);
        Assert.DoesNotContain(lines, line => line.Contains("Availability:", StringComparison.Ordinal) && line.Contains("Reason:", StringComparison.Ordinal));
        if (availability != "post-implementation") Assert.DoesNotContain(lines, line => line.StartsWith("Evidence owner:", StringComparison.Ordinal));
        var summary = OwnerQuestionResolutionText.Summary(resolution, TimeZoneInfo.Utc);
        Assert.Equal(resolution.Stage + ": Planner evidence request for criterion 1 — the operator, 00:00:00 (Answered)", summary);
        foreach (var label in new[] { "Availability:", "Owner:", "Reason:", "\n", "\r" }) Assert.DoesNotContain(label, summary);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AccompanyingBlockerLineFollowsReasonUnchanged(string newline)
    {
        const string accompanying = "Accompanying WORKER_RESULT blocker evidence: x";
        var question = Question("post-implementation") + newline + accompanying;
        var lines = OwnerQuestionResolutionText.Build(Resolution(question), TimeZoneInfo.Utc).Split('\n');
        var reasonAt = Array.IndexOf(lines, "Reason: " + Reason);
        Assert.True(reasonAt >= 0);
        Assert.Equal(accompanying, lines[reasonAt + 1]);
        Assert.Single(lines, line => line == accompanying);
    }

    [Fact]
    public async Task GoalDetailCompactChoiceOpensFieldLayoutAndWrapsSeparateParagraphs()
    {
        using var scene = new OwnerQuestionResolutionDialogTests.Scene();
        var request = scene.Harness.Kernel.RequestHumanInput(scene.Goal.Id, scene.Goal.Tasks[0].Id,
            Question("post-implementation"), isDismissible: true, kind: HumanWaitKind.ProspectiveAcceptanceEvidence);
        scene.Harness.Kernel.DismissHumanInput(request.Id);
        var entry = Assert.Single(await scene.Reader.ListForGoalAsync(scene.Goal.Id.Value, CancellationToken.None));
        var zone = scene.Harness.Clock.LocalTimeZone;
        var summary = OwnerQuestionResolutionText.Summary(entry, zone);
        Assert.Equal(entry.Stage + ": Planner evidence request for criterion 1 — " + entry.Answerer +
            $", {TimeZoneInfo.ConvertTime(entry.ResolvedAt, zone):HH:mm:ss} (Dismissed)", summary);
        scene.Dialogs.OnPage = (text, choiceLines) =>
        {
            var choiceLine = Assert.Single(choiceLines);
            Assert.Equal(summary, text.Replace("\r", "").Split('\n')[choiceLine]);
            using var dialog = new OwnerConsoleTextDialog("Goal", text, choiceLines);
            dialog.Format(60, 10);
            var selectedRow = dialog.Page.SourceLines.ToList().IndexOf(choiceLine);
            Assert.True(selectedRow >= 0);
            for (var row = 0; row < selectedRow; row++) Assert.True(dialog.HandleKey(Key.CursorDown));
            int? selected = null;
            dialog.ChoiceAccepted += index => selected = index;
            Assert.True(dialog.HandleKey(Key.Enter));
            Assert.Equal(0, selected);
            return selected;
        };
        await scene.Controller.ShowGoalDetailAsync(scene.Goal.Id.Value);
        Assert.Equal(2, scene.Dialogs.Texts.Count);
        Assert.Equal("Goal", scene.Dialogs.Texts[0].Title);
        Assert.Equal("Question resolution", scene.Dialogs.Texts[1].Title);
        var buildText = OwnerQuestionResolutionText.Build(entry, zone);
        Assert.Equal(buildText, scene.Dialogs.Texts[1].Text);
        var page = new OwnerConsoleTextPage(buildText, 40, 50);
        var sources = new List<int>();
        foreach (var label in new[] { "Availability: ", "Evidence owner: ", "Reason: ", "Resolution: " })
        {
            var index = page.Lines.ToList().FindIndex(line => line.StartsWith(label, StringComparison.Ordinal));
            Assert.True(index >= 0, "Missing field paragraph: " + label);
            sources.Add(page.SourceLines[index]);
        }
        Assert.Equal(4, sources.Distinct().Count());
        Assert.All(page.Lines, line => Assert.True(line.GetColumns() <= 40));
    }

    [Fact]
    public void ProseQuestionKeepsOriginalQuestionAndSummary()
    {
        const string question = "Which direction?\nKeep all the question context.";
        var resolution = Resolution(question);
        var text = OwnerQuestionResolutionText.Build(resolution, TimeZoneInfo.Utc);
        Assert.Contains("\nQuestion:\n" + question + "\nResolution: Answered by the operator at 00:00:00\n", text);
        Assert.Equal(resolution.Stage + ": Which direction? — the operator, 00:00:00 (Answered)",
            OwnerQuestionResolutionText.Summary(resolution, TimeZoneInfo.Utc));
    }

    [Fact]
    public void MalformedAndMergedRequestsFallBackToProse()
    {
        var request = Question("post-implementation");
        string[] questions = [
            "Planner evidence request for criterion 1: x",
            "Merged evidence requests: " + request + "; " + Question("never-recorded"),
            request + "; " + Question("never-recorded"),
            request + " Merged evidence requests:",
            "Planner evidence request for criterion 1: x. Availability: never-recorded",
            "Ordinary question. Availability: x. Reason: y"];
        foreach (var question in questions)
        {
            Assert.Null(OwnerEvidenceRequestFields.Read(question));
            Assert.Contains("\nQuestion:\n" + question + "\n", OwnerQuestionResolutionText.Build(Resolution(question), TimeZoneInfo.Utc));
            Assert.Equal(Resolution(question).Stage + ": " + question + " — the operator, 00:00:00 (Answered)",
                OwnerQuestionResolutionText.Summary(Resolution(question), TimeZoneInfo.Utc));
        }
    }

    private static OwnerQuestionResolution Resolution(string question) => new("q1", "11111111", "Readable questions",
        "Planner clarification on task 8366d892", question, "Answered", "the operator", At, null, [], "Work continues.");

    private static string Question(string availability)
    {
        var payload = new Dictionary<string, object>
        {
            ["criterion_index"] = 1,
            ["evidence_key"] = "layout-proof",
            ["availability"] = availability,
            ["needed"] = Needed,
            ["reason"] = Reason
        };
        if (availability == "post-implementation") payload["owner"] = "acceptance-gate";
        if (availability == "retrievable") payload["store"] = "operator-records";
        var parsed = AgentOutputDirectives.ParseHumanInputRequest("PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(payload), AgentRole.Planner);
        Assert.NotNull(parsed.Directive);
        return parsed.Directive!.Question;
    }
}
