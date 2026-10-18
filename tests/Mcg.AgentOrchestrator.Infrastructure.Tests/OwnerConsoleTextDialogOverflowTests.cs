using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: each dialog and standalone page is local; no initialized application.
public sealed class OwnerConsoleTextDialogOverflowTests
{
    [Fact]
    public void Paging_LongBody_UpdatesTitleAndPreservesStandalonePageState()
    {
        var title = string.Concat(Enumerable.Range(0, 150).Select(index => (char)('a' + index % 26)));
        var text = "Title: " + title + "\nAnswer:\n" + new string('a', 400);
        using var dialog = new OwnerConsoleTextDialog("Goal", text);
        var baseline = new OwnerConsoleTextPage(text, 60, 2);
        dialog.Format(60, 2);
        var count = dialog.Page.Lines.Count;
        Assert.True(count > 3);
        Assert.Equal($"Goal · {count - 2} below", dialog.Title);
        AssertPage(baseline, dialog.Page);

        Assert.True(dialog.HandleKey(Key.PageDown));
        Assert.True(baseline.HandleKey(Key.PageDown));
        var offset = dialog.Page.Offset;
        Assert.True(offset > 0 && offset < count - 2);
        Assert.Equal($"Goal · {offset} above · {count - offset - 2} below", dialog.Title);
        AssertPage(baseline, dialog.Page);

        var stopped = false;
        for (var attempt = 0; attempt < count; attempt++)
        {
            var before = dialog.Page.Offset;
            Assert.True(dialog.HandleKey(Key.PageDown));
            Assert.True(baseline.HandleKey(Key.PageDown));
            AssertPage(baseline, dialog.Page);
            if (dialog.Page.Offset != before) continue;
            stopped = true;
            break;
        }
        Assert.True(stopped, "PageDown must reach the last loaded row.");
        Assert.Equal(count - 2, dialog.Page.Offset);
        Assert.Equal($"Goal · {count - 2} above", dialog.Title);
    }

    [Fact]
    public void Paging_FittingBody_KeepsBareTitleAndStandalonePageState()
    {
        const string text = "short\nsecond";
        using var dialog = new OwnerConsoleTextDialog("Goal", text);
        var baseline = new OwnerConsoleTextPage(text, 60, 2);
        dialog.Format(60, 2);
        Assert.Equal("Goal", dialog.Title);
        AssertPage(baseline, dialog.Page);
        foreach (var key in new[] { Key.PageDown, Key.PageUp })
        {
            Assert.True(dialog.HandleKey(new Key(key)));
            Assert.True(baseline.HandleKey(new Key(key)));
            Assert.Equal("Goal", dialog.Title);
            AssertPage(baseline, dialog.Page);
        }
    }

    private static void AssertPage(OwnerConsoleTextPage expected, OwnerConsoleTextPage actual)
    {
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.Visible, actual.Visible);
        Assert.Equal(expected.Offset, actual.Offset);
    }
}
