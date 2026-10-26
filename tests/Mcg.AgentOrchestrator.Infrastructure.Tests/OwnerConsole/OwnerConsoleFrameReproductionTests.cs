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
    public Task PageUpPageDownAndScrollCommitWholeFrames() => PagingFrames(buffered: true);

    [Fact]
    public Task PreFixOutputBaseExposesRowCommits() => PagingFrames(buffered: false);

    private async Task PagingFrames(bool buffered)
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
        // OutputBase dirty-row emitter, not Terminal.Gui's full layout or native ANSI backend.
        var terminal = new RecordingOutput(width, height);
        var destination = buffered ? OwnerConsoleFrameOutputInstaller.BufferOutput(terminal, TextWriter.Null) : terminal;
        Assert.Equal(buffered, destination is OwnerConsoleFrameOutput);
        Key[] keys = [Key.PageDown, Key.PageUp, .. Enumerable.Repeat(Key.CursorDown, 12)];
        int[] selected = [10, 0, .. Enumerable.Range(1, 12)];
        output.WriteLine(JsonSerializer.Serialize(new
        {
            listSize = count, simulatedWidth = width, simulatedHeight = height,
            backend = buffered ? "Production frame adapter with recording ANSI output (Windows VT-equivalent commits)" :
                "OutputBase with AnsiOutput Windows VT-equivalent immediate cursor/content commit recorder",
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
            destination.Write(buffer); // Real pinned dependency implementation, not a copied row loop.
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

            if (buffered)
            {
                var commit = Assert.Single(terminal.Operations);
                Assert.Equal("frame", commit.Kind);
                Assert.DoesNotContain("\u001b[2J", commit.Payload);
                Assert.DoesNotContain("\u001b[3J", commit.Payload);
                Assert.Equal(expected, Assert.Single(terminal.ScreensAfterContent));
            }
            else
            {
                // RED mechanism arm: without the production adapter the same buffer commits per row.
                Assert.Equal(2 * height, terminal.Operations.Count);
                for (var row = 0; row < height; row++)
                {
                    Assert.Equal("cursor", terminal.Operations[2 * row].Kind);
                    Assert.Equal(row, terminal.Operations[2 * row].Row);
                    Assert.Equal("content", terminal.Operations[2 * row + 1].Kind);
                }
                Assert.Equal(expected[0], terminal.ScreensAfterContent[0][0]);
                Assert.Equal(before[^1], terminal.ScreensAfterContent[0][^1]);
            }
            Assert.Equal(expected, terminal.Screen);
            Assert.NotEqual(before[^1], expected[^1]);
            Assert.All(buffer.DirtyLines, dirty => Assert.False(dirty));
        }
    }

    internal sealed record Operation(string Kind, int Column, int Row, string Payload);

    // AnsiOutput's Windows VT branch submits cursor and content separately through WriteFile.
    // Record that mapping without claiming to intercept AnsiOutput or perform native IO.
    internal sealed class RecordingOutput(int width, int height) : OutputBase, IOutput
    {
        private int _column, _row;
        internal string[] Screen { get; private set; } = Enumerable.Repeat(new string(' ', width), height).ToArray();
        internal List<Operation> Operations { get; } = [];
        internal List<string[]> ScreensAfterContent { get; } = [];
        internal bool Disposed { get; private set; }
        internal bool RecordOnly { get; set; }
        internal bool FailNextWrite { get; set; }

        internal void ResetOperations() { Operations.Clear(); ScreensAfterContent.Clear(); }

        protected override bool SetCursorPositionImpl(int screenPositionX, int screenPositionY)
        {
            (_column, _row) = (screenPositionX, screenPositionY);
            Operations.Add(new("cursor", _column, _row, $"\u001b[{_row + 1};{_column + 1}H"));
            return true;
        }

        protected override void Write(StringBuilder text)
        {
            base.Write(text);
            var payload = text.ToString();
            Operations.Add(new("content", _column, _row, payload));
            if (RecordOnly) return;
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

        public void Write(ReadOnlySpan<char> text)
        {
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("recorded write failure"); }
            // Match AnsiOutput: span writes use OutputBase's real last-output recorder too.
            base.Write(new StringBuilder().Append(text));
            var payload = text.ToString();
            Operations.Add(new("frame", _column, _row, payload));
            if (RecordOnly) return;
            // Consume a whole native submission before exposing the screen at its commit boundary.
            var plain = Regex.Replace(payload, @"\x1b\[[0-9;]*m", "");
            var matches = Regex.Matches(plain, @"\x1b\[(\d+);(\d+)H([^\x1b]*)");
            Assert.NotEmpty(matches);
            Assert.Equal(plain.Length, matches.Sum(match => match.Length));
            foreach (Match match in matches)
            {
                _row = int.Parse(match.Groups[1].Value) - 1;
                _column = int.Parse(match.Groups[2].Value) - 1;
                var content = match.Groups[3].Value;
                Assert.InRange(_row, 0, height - 1);
                Assert.InRange(_column + content.Length, 0, width);
                var row = Screen[_row].ToCharArray();
                content.CopyTo(0, row, _column, content.Length);
                Screen[_row] = new string(row);
                _column += content.Length;
            }
            ScreensAfterContent.Add(Screen.ToArray());
        }

        public Size GetSize() => new(width, height);
        public void SetSize(int columns, int rows)
        {
            width = columns; height = rows;
            Screen = Enumerable.Repeat(new string(' ', width), height).ToArray();
        }
        private Terminal.Gui.Drivers.Cursor _cursor = new();
        public Terminal.Gui.Drivers.Cursor GetCursor() => _cursor;
        public void SetCursor(Terminal.Gui.Drivers.Cursor cursor) => _cursor = cursor;
        public void Suspend() { }
        public void Dispose() => Disposed = true;
    }
}
