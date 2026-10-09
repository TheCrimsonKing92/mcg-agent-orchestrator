using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

// Parallel-safe: isolated scene stores and dialog models; no initialized application.
public sealed class OwnerConsoleTextPageTests
{
    [Theory(DisplayName = "Meaning, goal detail and resolution dialogs preserve full text at narrow and wide widths")]
    [InlineData(60)]
    [InlineData(200)]
    public async Task DialogsWrapFullTextAndPageToUnseenContent(int width)
    {
        var title = string.Concat(Enumerable.Range(0, 150).Select(index => (char)('a' + index % 26)));
        var answer = string.Concat(Enumerable.Range(0, 400).Select(index => (char)('A' + index % 26)));
        using var scene = new OwnerQuestionResolutionDialogTests.Scene(title);
        var id = await scene.ResolveSpec("Which direction?", answer, OwnerQuestionResolutionDialogTests.Scene.At);
        await scene.Controller.ShowActivityMeaningAsync(scene.Activity(id, "Which direction?", OwnerQuestionResolutionDialogTests.Scene.At));
        scene.Dialogs.OnPage = (_, _) => 0;
        await scene.Controller.ShowGoalDetailAsync(scene.Goal.Id.Value);
        Assert.Equal(new[] { "What this means", "Goal", "Question resolution" }, scene.Dialogs.Texts.Select(item => item.Title));
        foreach (var captured in scene.Dialogs.Texts)
        {
            using var dialog = new OwnerConsoleTextDialog(captured.Title, captured.Text);
            dialog.Format(width, 2);
            Assert.All(dialog.Page.Lines, line =>
            {
                Assert.DoesNotContain("…", line);
                Assert.True(line.GetColumns() <= width, $"Line exceeds {width} cells: {line}");
            });
            var all = string.Concat(dialog.Page.Lines);
            Assert.Contains(title, all);
            if (captured.Title != "Goal") Assert.Contains(answer, all);
            var before = dialog.Page.Visible.ToArray();
            Assert.True(dialog.HandleKey(Key.PageDown));
            Assert.True(dialog.Page.Offset > 0);
            Assert.Contains(dialog.Page.Visible, line => !before.Contains(line));
            Assert.True(dialog.HandleKey(Key.PageUp));
            Assert.Equal(0, dialog.Page.Offset);
        }
    }

    [Theory(DisplayName = "Every dialog body wraps long words without losing characters")]
    [InlineData("What this means", 60)]
    [InlineData("Goal", 60)]
    [InlineData("Question resolution", 60)]
    [InlineData("Decision", 60)]
    [InlineData("What this means", 200)]
    [InlineData("Goal", 200)]
    [InlineData("Question resolution", 200)]
    [InlineData("Decision", 200)]
    public void LongBodySurvivesWrappingAndScrolling(string dialogTitle, int width)
    {
        // Distinct wrapped title rows make newly visible text observable after paging.
        var title = string.Concat(Enumerable.Range(0, 150).Select(index => (char)('a' + index % 26)));
        var answer = new string('a', 400);
        var text = "Title: " + title + "\nAnswer:\n" + answer;
        using var dialog = new OwnerConsoleTextDialog(dialogTitle, text);
        dialog.Format(width, 2);
        Assert.Equal(text.Replace("\n", ""), string.Concat(dialog.Page.Lines));
        Assert.All(dialog.Page.Lines, line =>
        { Assert.DoesNotContain("…", line); Assert.True(line.GetColumns() <= width); });
        var before = dialog.Page.Visible.ToArray();
        var beforeOffset = dialog.Page.Offset;
        Assert.True(dialog.HandleKey(Key.PageDown));
        Assert.True(dialog.Page.Offset > beforeOffset);
        Assert.Contains(dialog.Page.Visible, line => !before.Contains(line));
        Assert.True(dialog.HandleKey(Key.CursorDown));
        Assert.True(dialog.HandleKey(Key.CursorUp));
    }

    [Fact(DisplayName = "Unicode wrapping measures terminal cells and preserves content across resize")]
    public void UnicodeWrapUsesCellWidthAndPreservesCharacters()
    {
        var text = "界界界e\u0301🙂 title\nnext";
        var page = new OwnerConsoleTextPage(text, 6, 2);
        Assert.All(page.Lines, line => Assert.True(line.GetColumns() <= 6));
        Assert.Equal(text.Replace("\n", ""), string.Concat(page.Lines));
        page.HandleKey(Key.PageDown);
        page.Resize(200, 3);
        Assert.Equal(text.Replace("\n", ""), string.Concat(page.Lines));
        Assert.All(page.Visible, line => Assert.True(line.GetColumns() <= 200));
    }
}
