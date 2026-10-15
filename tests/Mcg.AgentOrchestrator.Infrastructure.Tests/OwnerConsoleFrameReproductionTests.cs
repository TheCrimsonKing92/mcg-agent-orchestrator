using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

// Parallel-safe: uninitialized instance application, local buffer and recorder; no real terminal IO.
public sealed class OwnerConsoleFrameReproductionTests(Xunit.ITestOutputHelper output)
{
    [Fact]
    public async Task PagingExposesRowCommitsInPinnedOutputBase()
    {
        const int count = 60, width = 72, height = 10;
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        for (var index = 0; index < count; index++)
            scene.Harness.AddGoal(index.ToString("D8"), $"Goal {index:D2}", AgentRole.Developer);
        await scene.Render([]);
        var headers = 1 + (scene.View.BoardPane.Style.ShowHorizontalHeaderOverline ? 1 : 0) +
            (scene.View.BoardPane.Style.ShowHorizontalHeaderUnderline ? 1 : 0);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, width, height + headers);
        scene.View.FocusDecisions();
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.Home);

        // Deliberately project ASCII table rows into a simulated viewport. This exercises the real
        // OutputBase dirty-row emitter, not Terminal.Gui's full layout or native Windows backend.
        var terminal = new RecordingOutput(width, height);
        Key[] keys = [Key.PageDown, Key.PageUp, .. Enumerable.Repeat(Key.CursorDown, 12)];
        int[] selected = [10, 0, .. Enumerable.Range(1, 12)];
        output.WriteLine(JsonSerializer.Serialize(new
        {
            listSize = count, simulatedWidth = width, simulatedHeight = height,
            backend = "OutputBase with Windows-equivalent immediate cursor/content commit recorder",
            clockFrequency = Stopwatch.Frequency, explicitFlushCount = 0,
            limitation = "Synthetic ASCII viewport; not a native write, live timing or visual receipt."
        }));
        for (var frame = 0; frame < keys.Length; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            await scene.View.HandleKeyAsync(keys[frame]);
            var inputTicks = Stopwatch.GetTimestamp() - started;

            started = Stopwatch.GetTimestamp();
            var model = await scene.Builder.BuildAsync(new(scene.Harness.Clock.GetUtcNow(), null, [], 0));
            var prepareTicks = Stopwatch.GetTimestamp() - started;

            started = Stopwatch.GetTimestamp();
            scene.View.Render(model); // Includes the production OwnerConsoleBoardRenderer.
            var selection = scene.View.BoardPane.Value!.SelectedCell.Y;
            Assert.Equal(selected[frame], selection);
            Assert.Equal(OwnerConsolePane.Board, scene.View.FocusedPane);
            var first = Math.Clamp(selection, 0, count - height);
            var expected = Enumerable.Range(first, height).Select(index =>
                $"{scene.View.BoardTable.Rows[index][0]} {scene.View.BoardTable.Rows[index][2]}".PadRight(width)).ToArray();
            var buffer = new OutputBufferImpl();
            buffer.SetSize(width, height);
            for (var row = 0; row < height; row++)
            {
                buffer.Move(0, row);
                buffer.AddStr(expected[row]);
            }
            var renderTicks = Stopwatch.GetTimestamp() - started;

            var before = terminal.Screen.ToArray();
            terminal.ResetOperations();
            started = Stopwatch.GetTimestamp();
            terminal.Write(buffer); // Real pinned dependency implementation, not a copied row loop.
            var writeTicks = Stopwatch.GetTimestamp() - started;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                frame, key = keys[frame].ToString(), selection, first, inputTicks, prepareTicks,
                renderAndProjectionTicks = renderTicks, writeAndRecorderTicks = writeTicks,
                writeCount = terminal.Operations.Count, explicitFlushCount = 0,
                clearScreenCount = terminal.Operations.Count(operation =>
                    operation.Payload.Contains("\u001b[2J", StringComparison.Ordinal) ||
                    operation.Payload.Contains("\u001b[3J", StringComparison.Ordinal) ||
                    operation.Payload.Contains("\u001bc", StringComparison.Ordinal)),
                operations = terminal.Operations
            }));

            // Characterization of the unfixed mechanism, NOT the requested one-commit regression.
            Assert.Equal(2 * height, terminal.Operations.Count);
            for (var row = 0; row < height; row++)
            {
                Assert.Equal("cursor", terminal.Operations[2 * row].Kind);
                Assert.Equal(row, terminal.Operations[2 * row].Row);
                Assert.Equal("content", terminal.Operations[2 * row + 1].Kind);
            }
            Assert.Equal(expected, terminal.Screen);
            Assert.NotEqual(before[^1], expected[^1]);
            Assert.Equal(expected[0], terminal.ScreensAfterContent[0][0]);
            Assert.Equal(before[^1], terminal.ScreensAfterContent[0][^1]);
            Assert.All(buffer.DirtyLines, dirty => Assert.False(dirty));
        }
    }

    private sealed record Operation(string Kind, int Column, int Row, string Payload);

    // WindowsOutput immediately submits both operations to WriteConsole. Record that mapping,
    // without claiming to intercept WindowsOutput (which is internal and performs native IO).
    private sealed class RecordingOutput(int width, int height) : OutputBase
    {
        private int _column, _row;
        internal string[] Screen { get; } = Enumerable.Repeat(new string(' ', width), height).ToArray();
        internal List<Operation> Operations { get; } = [];
        internal List<string[]> ScreensAfterContent { get; } = [];

        internal void ResetOperations() { Operations.Clear(); ScreensAfterContent.Clear(); }

        protected override bool SetCursorPositionImpl(int screenPositionX, int screenPositionY)
        {
            (_column, _row) = (screenPositionX, screenPositionY);
            Operations.Add(new("cursor", _column, _row, $"\u001b[{_row + 1};{_column + 1}H"));
            return true;
        }

        protected override void Write(StringBuilder text)
        {
            var payload = text.ToString();
            Operations.Add(new("content", _column, _row, payload));
            // This fixture uses only ASCII cells and SGR attributes; reject unsupported escapes.
            var plain = Regex.Replace(payload, @"\x1b\[[0-9;]*m", "");
            Assert.DoesNotContain('\u001b', plain);
            Assert.InRange(_column + plain.Length, 0, width);
            var row = Screen[_row].ToCharArray();
            plain.CopyTo(0, row, _column, plain.Length);
            Screen[_row] = new string(row);
            _column += plain.Length;
            ScreensAfterContent.Add(Screen.ToArray());
        }
    }
}
