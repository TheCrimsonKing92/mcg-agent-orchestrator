using System.Drawing;
using System.Text;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

// Parallel-safe: isolated scenes, recording output, no initialized driver or process-global streams.
public sealed class OwnerConsoleFrameCompatibilityTests
{
    [Fact]
    public void ResizeRepaintsCorrectlySizedBuffersAsOneSubmission()
    {
        var native = new OwnerConsoleFrameReproductionTests.RecordingOutput(72, 10);
        using var destination = OwnerConsoleFrameOutputInstaller.BufferOutput(native, TextWriter.Null);
        Assert.IsType<OwnerConsoleFrameOutput>(destination);
        foreach (var (width, height) in new[] { (72, 10), (40, 5), (96, 15), (12, 1) })
        {
            destination.SetSize(width, height);
            Assert.Equal(new Size(width, height), destination.GetSize());
            var rows = Enumerable.Range(0, height).Select(row => $"Row {row:D2}".PadRight(width)).ToArray();
            var buffer = Buffer(rows);
            Assert.Equal(width, buffer.Cols);
            Assert.Equal(height, buffer.Rows);
            native.ResetOperations();

            destination.Write(buffer);

            Assert.Single(native.Operations);
            Assert.Equal(rows, Assert.Single(native.ScreensAfterContent));
            Assert.Equal(rows, native.Screen);
            Assert.All(buffer.DirtyLines, dirty => Assert.False(dirty));
        }
    }

    [Fact]
    public async Task RepeatedPagingKeepsIdentityFocusAndViewportBoundsAcrossRepaintAndResize()
    {
        const int width = 72, height = 10, count = 60;
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        for (var index = 0; index < count; index++)
            scene.Harness.AddGoal(index.ToString("D8"), $"Goal {index:D2}", AgentRole.Developer);
        await scene.Render([]);
        var headers = 1 + (scene.View.BoardPane.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (scene.View.BoardPane.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, width, height + headers);
        scene.View.FocusDecisions();
        await scene.View.HandleKeyAsync(new Key(Key.Tab));
        var native = new OwnerConsoleFrameReproductionTests.RecordingOutput(width, height);
        using var destination = OwnerConsoleFrameOutputInstaller.BufferOutput(native, TextWriter.Null);
        Key[] keys = [Key.Home, .. Enumerable.Repeat(Key.PageDown, 8), Key.CursorDown,
            .. Enumerable.Repeat(Key.PageUp, 8), Key.CursorUp];
        int[] indices = [0, 10, 20, 30, 40, 50, 59, 59, 59, 59, 49, 39, 29, 19, 9, 0, 0, 0, 0];
        Assert.Equal(keys.Length, indices.Length);
        for (var frame = 0; frame < keys.Length; frame++)
        {
            await scene.View.HandleKeyAsync(new Key(keys[frame]));
            Assert.Equal(indices[frame], scene.View.BoardPane.Value!.SelectedCell.Y);
            var identity = scene.View.SelectedGoalId;
            var offset = scene.View.BoardPane.RowOffset;
            Assert.InRange(offset, 0, count - height);
            Assert.InRange(indices[frame] - offset, 0, height - 1);
            await scene.Render([]);
            Assert.Equal(identity, scene.View.SelectedGoalId);
            Assert.Equal(offset, scene.View.BoardPane.RowOffset);
            Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);
            var rows = Enumerable.Range(offset, height).Select(row =>
                $"{scene.View.BoardTable.Rows[row][0]} {scene.View.BoardTable.Rows[row][2]}".PadRight(width)).ToArray();
            native.ResetOperations();

            destination.Write(Buffer(rows));

            Assert.Single(native.Operations);
            Assert.Equal(rows, native.Screen);
        }
        var selected = scene.View.SelectedGoalId;
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 40, 5 + headers);
        await scene.Render([]);
        Assert.Equal(selected, scene.View.SelectedGoalId);
        Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);
        Assert.Equal(40, scene.View.BoardPane.Viewport.Width);
    }

    [Fact]
    public void BufferedBytesMatchPreFixCursorAndContentSubmissionsIncludingAttributes()
    {
        string[] rows = ["first row   ", "second row  ", "third row   "];
        var oldOutput = new OwnerConsoleFrameReproductionTests.RecordingOutput(12, 3);
        oldOutput.Write(Buffer(rows));
        var native = new OwnerConsoleFrameReproductionTests.RecordingOutput(12, 3);
        using var destination = OwnerConsoleFrameOutputInstaller.BufferOutput(native, TextWriter.Null);

        destination.Write(Buffer(rows));

        Assert.Equal(string.Concat(oldOutput.Operations.Select(operation => operation.Payload)),
            Assert.Single(native.Operations).Payload);
        Assert.Equal(oldOutput.Screen, native.Screen);
        Assert.Equal(oldOutput.GetLastOutput(), destination.GetLastOutput());
        native.ResetOperations();
        var clean = Buffer(rows);
        destination.Write(clean);
        native.ResetOperations();
        destination.Write(clean);
        Assert.Empty(native.Operations); // No dirty content is not an empty frame commit.
    }

    [Fact]
    public void UnicodeAndColorSequencesRemainByteEquivalentToUnbufferedOutputBase()
    {
        var oldOutput = new OwnerConsoleFrameReproductionTests.RecordingOutput(32, 2) { RecordOnly = true };
        var native = new OwnerConsoleFrameReproductionTests.RecordingOutput(32, 2) { RecordOnly = true };
        using var destination = OwnerConsoleFrameOutputInstaller.BufferOutput(native, TextWriter.Null);
        static OutputBufferImpl UnicodeBuffer()
        {
            var buffer = new OutputBufferImpl();
            buffer.SetSize(32, 2);
            buffer.Move(0, 0); buffer.AddStr("界 e\u0301 🙂");
            buffer.Move(3, 1); buffer.AddStr("end");
            return buffer;
        }
        oldOutput.Write(UnicodeBuffer());

        destination.Write(UnicodeBuffer());

        var payload = Assert.Single(native.Operations).Payload;
        Assert.Contains("界", payload);
        Assert.Contains("e\u0301", payload);
        Assert.Contains("🙂", payload);
        Assert.Equal(Encoding.UTF8.GetBytes(string.Concat(oldOutput.Operations.Select(operation => operation.Payload))),
            Encoding.UTF8.GetBytes(payload));
    }

    [Fact]
    public void CursorPropertiesAndDirectQueriesKeepBuiltInLifecycle()
    {
        var native = new OwnerConsoleFrameReproductionTests.RecordingOutput(20, 2) { RecordOnly = true };
        using var destination = OwnerConsoleFrameOutputInstaller.BufferOutput(native, TextWriter.Null);
        destination.Force16Colors = true;
        Assert.True(native.Force16Colors);
        var cursor = new Terminal.Gui.Drivers.Cursor();
        destination.SetCursor(cursor);
        Assert.Equal(cursor, destination.GetCursor());

        destination.Write("\u001b[6n".AsSpan());

        Assert.Equal("\u001b[6n", Assert.Single(native.Operations).Payload);
    }

    [Fact]
    public void FailedSubmissionDoesNotLeakBufferedContentIntoNextFrameAndDisposalIsForwarded()
    {
        var native = new OwnerConsoleFrameReproductionTests.RecordingOutput(12, 2) { FailNextWrite = true };
        var destination = OwnerConsoleFrameOutputInstaller.BufferOutput(native, TextWriter.Null);
        Assert.Throws<IOException>(() => destination.Write(Buffer(["old first   ", "old last    "])));
        Assert.Empty(native.Operations);

        destination.Write(Buffer(["new first   ", "new last    "]));

        Assert.DoesNotContain("old", Assert.Single(native.Operations).Payload);
        Assert.Equal(new[] { "new first   ", "new last    " }, native.Screen);
        destination.Dispose();
        Assert.True(native.Disposed);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task PlainAndRedirectedBoardRetainExactBaselineBytes(bool plain, bool inputRedirected, bool outputRedirected)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111", "Build search", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Timeline = goal.Timeline.Select(item => item with { OccurredAt = harness.Clock.GetUtcNow() }).ToArray()
            }).ToArray()
        });
        // Pre-change board byte contract (session output and routing are untouched by this adapter).
        var expected = ConsoleAnnouncementFormatter.Format(harness.Clock, "board | active goals: 1 | owner questions: 0") +
            Environment.NewLine + "11111111 | Build search | Active | Developer | 0s" + Environment.NewLine;
        var calls = 0;
        var runners = new OwnerConsoleModeRunners(async token =>
        {
            calls++;
            await harness.Session().HandleCommandAsync("board", token);
            return 0;
        }, _ => throw new InvalidOperationException("Full-screen output reached plain routing."));

        Assert.Equal(0, await OwnerConsoleModeSelector.RunAsync(plain ? ["console", "--plain"] : ["console"],
            inputRedirected, outputRedirected, runners));

        Assert.Equal(1, calls);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(harness.Output.Text));
        Assert.DoesNotContain('\u001b', harness.Output.Text);
    }

    internal static OutputBufferImpl Buffer(string[] rows)
    {
        var buffer = new OutputBufferImpl();
        buffer.SetSize(rows[0].Length, rows.Length);
        for (var row = 0; row < rows.Length; row++)
        {
            buffer.Move(0, row);
            buffer.AddStr(rows[row]);
        }
        return buffer;
    }
}
