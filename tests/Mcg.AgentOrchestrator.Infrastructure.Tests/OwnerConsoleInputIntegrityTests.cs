using System.Collections.Concurrent;
using System.Text;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each fact owns its key source, reader, buffer and output sink.
public sealed class OwnerConsoleInputIntegrityTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;
    private static string NL => Environment.NewLine;

    [Fact]
    public async Task InjectedOutputBetweenKeysSubmitsDigestAndReprintsPartialInput()
    {
        using var keys = new ScriptedKeys();
        var sink = new RecordingSink();
        var editor = new OwnerConsoleLineEditor(keys, sink);
        var input = new SystemConsoleInput(editor);
        var output = new SystemConsoleOutput(editor);
        var read = input.ReadLineAsync(TestToken).AsTask();

        await keys.TypeAsync("di");
        Assert.True(input.IsEditingLine);
        output.WriteLine("EVENT");
        Assert.Equal("di" + NL + "EVENT" + NL + "di", sink.Text);
        await keys.TypeAsync("gest");
        await keys.EnterAsync();

        Assert.Equal("digest", await read.WaitAsync(TestToken));
        Assert.Equal("di" + NL + "EVENT" + NL + "digest" + NL, sink.Text);
        Assert.False(input.IsEditingLine);
    }

    [Fact]
    public async Task AbandonedReadDoesNotLoseOrDuplicateTheNextLine()
    {
        using var keys = new ScriptedKeys();
        var sink = new RecordingSink();
        var editor = new OwnerConsoleLineEditor(keys, sink);
        var input = new SystemConsoleInput(editor);
        using var abandonedToken = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        var abandoned = input.ReadLineAsync(abandonedToken.Token).AsTask();
        await keys.TypeAsync("di");
        abandonedToken.Cancel();

        // This read is abandoned while the single key source is blocked awaiting another key.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TestToken));
        var next = input.ReadLineAsync(TestToken).AsTask();
        await keys.TypeAsync("gest");
        await keys.EnterAsync();
        Assert.Equal("digest", await next.WaitAsync(TestToken));

        var following = input.ReadLineAsync(TestToken).AsTask();
        await keys.TypeAsync("help");
        await keys.EnterAsync();
        Assert.Equal("help", await following.WaitAsync(TestToken));

        // End the controlled stream, then observe EOF to prove no duplicate line was queued.
        keys.Complete();
        Assert.Null(await input.ReadLineAsync(TestToken));
        Assert.Single(keys.ReaderThreads);
        Assert.Equal("digest" + NL + "help" + NL, sink.Text);
    }

    [Fact]
    public async Task BackspaceAcrossInjectedOutputKeepsVisibleEqualSubmitted()
    {
        using var keys = new ScriptedKeys();
        var sink = new RecordingSink();
        var editor = new OwnerConsoleLineEditor(keys, sink);
        var input = new SystemConsoleInput(editor);
        var output = new SystemConsoleOutput(editor);
        var read = input.ReadLineAsync(TestToken).AsTask();

        await keys.TypeAsync("dx");
        output.WriteLine("EVENT");
        await keys.BackspaceAsync();
        await keys.TypeAsync("igest");
        await keys.EnterAsync();

        var submitted = await read.WaitAsync(TestToken);
        Assert.Equal("digest", submitted);
        Assert.Equal("dx" + NL + "EVENT" + NL + "dx\b \bigest" + NL, sink.Text);
        Assert.Equal(submitted, RenderLastCompletedLine(sink.Text));

        var next = input.ReadLineAsync(TestToken).AsTask();
        await keys.TypeAsync("dix");
        await keys.BackspaceAsync();
        output.Write("SECOND");
        Assert.EndsWith(NL + "SECOND" + NL + "di", sink.Text);
        await keys.TypeAsync("gest");
        await keys.EnterAsync();
        Assert.Equal("digest", await next.WaitAsync(TestToken));
        Assert.Equal("digest", RenderLastCompletedLine(sink.Text));
    }

    [Fact]
    public async Task OutputWithoutEditingAndBellRemainUnchanged()
    {
        using var keys = new ScriptedKeys();
        var sink = new RecordingSink();
        var editor = new OwnerConsoleLineEditor(keys, sink);
        var input = new SystemConsoleInput(editor);
        var output = new SystemConsoleOutput(editor);
        output.Write("plain");
        output.WriteLine(" line");
        Assert.Equal("plain line" + NL, sink.Text);
        Assert.Empty(keys.ReaderThreads);

        var read = input.ReadLineAsync(TestToken).AsTask();
        await keys.TypeAsync("di");
        output.Write("\a");
        Assert.Equal("plain line" + NL + "di\a", sink.Text);
        await keys.TypeAsync("gest");
        await keys.EnterAsync();
        Assert.Equal("digest", await read.WaitAsync(TestToken));
    }

    [Fact]
    public async Task NewlineOutputReprintsInputInsteadOfPassingThroughLikeTheBell()
    {
        using var keys = new ScriptedKeys();
        var sink = new RecordingSink();
        var editor = new OwnerConsoleLineEditor(keys, sink);
        var input = new SystemConsoleInput(editor);
        var output = new SystemConsoleOutput(editor);
        var read = input.ReadLineAsync(TestToken).AsTask();
        await keys.TypeAsync("di");
        output.Write("\n");
        Assert.Equal("di" + NL + "\n" + NL + "di", sink.Text);
        await keys.TypeAsync("gest");
        await keys.EnterAsync();
        Assert.Equal("digest", await read.WaitAsync(TestToken));
        Assert.Equal("digest", RenderLastCompletedLine(sink.Text));
    }

    [Fact]
    public async Task MultipleReadsAndQueuedLinesAreDeliveredInOrderExactlyOnce()
    {
        using var keys = new ScriptedKeys();
        var input = new SystemConsoleInput(new OwnerConsoleLineEditor(keys, new RecordingSink()));
        var first = input.ReadLineAsync(TestToken).AsTask();
        var second = input.ReadLineAsync(TestToken).AsTask();
        await keys.TypeAsync("digest");
        await keys.EnterAsync();
        await keys.TypeAsync("help");
        await keys.EnterAsync();
        Assert.Equal("digest", await first.WaitAsync(TestToken));
        Assert.Equal("help", await second.WaitAsync(TestToken));

        await keys.TypeAsync("quit");
        await keys.EnterAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => input.ReadLineAsync(cancelled.Token).AsTask());
        Assert.Equal("quit", await input.ReadLineAsync(TestToken));
        keys.Complete();
        Assert.Null(await input.ReadLineAsync(TestToken));
        Assert.Single(keys.ReaderThreads);
    }

    [Fact]
    public async Task BackspaceToEmptyAndIgnoredKeysPreserveEditingState()
    {
        using var keys = new ScriptedKeys();
        var sink = new RecordingSink();
        var editor = new OwnerConsoleLineEditor(keys, sink);
        var input = new SystemConsoleInput(editor);
        var output = new SystemConsoleOutput(editor);
        var read = input.ReadLineAsync(TestToken).AsTask();
        await keys.BackspaceAsync();
        await keys.TypeAsync("x");
        await keys.BackspaceAsync();
        await keys.SendAsync(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        Assert.False(input.IsEditingLine);
        output.WriteLine("EVENT");
        Assert.Equal("x\b \bEVENT" + NL, sink.Text);
        await keys.EnterAsync();
        Assert.Equal("", await read.WaitAsync(TestToken));
    }

    private static string RenderLastCompletedLine(string transcript)
    {
        var line = new StringBuilder();
        var cursor = 0;
        var completed = "";
        foreach (var character in transcript)
        {
            if (character == '\r') { cursor = 0; continue; }
            if (character == '\n')
            {
                completed = line.ToString();
                line.Clear();
                cursor = 0;
                continue;
            }
            if (character == '\b') { cursor = Math.Max(0, cursor - 1); continue; }
            if (character == '\a') continue;
            if (cursor < line.Length) line[cursor] = character;
            else line.Append(character);
            cursor++;
        }
        return completed;
    }

    private sealed class RecordingSink : IOwnerConsoleTextSink
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        internal string Text { get { lock (_gate) return _text.ToString(); } }
        public void Write(string text) { lock (_gate) _text.Append(text); }
    }

    private sealed class ScriptedKeys : IOwnerConsoleKeySource, IDisposable
    {
        private readonly BlockingCollection<Keystroke> _keys = new();
        private readonly ConcurrentDictionary<int, byte> _readerThreads = new();
        private readonly CancellationToken _testToken = TestToken;
        private TaskCompletionSource? _handled;
        public bool IsInputRedirected => false;
        internal IEnumerable<int> ReaderThreads => _readerThreads.Keys;

        public ConsoleKeyInfo? ReadKey()
        {
            _readerThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
            // Re-entering the source is the barrier proving echo/buffer handling has finished.
            _handled?.TrySetResult();
            try
            {
                var next = _keys.Take(_testToken);
                _handled = next.Handled;
                return next.Key;
            }
            catch (InvalidOperationException) when (_keys.IsCompleted) { return null; }
        }

        internal async Task TypeAsync(string text)
        {
            foreach (var character in text)
                await SendAsync(new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false));
        }

        internal Task EnterAsync() => SendAsync(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        internal Task BackspaceAsync() => SendAsync(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false));
        internal Task SendAsync(ConsoleKeyInfo key)
        {
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _keys.Add(new(key, handled), _testToken);
            return handled.Task.WaitAsync(_testToken);
        }

        internal void Complete() => _keys.CompleteAdding();
        public void Dispose() => Complete();
        private sealed record Keystroke(ConsoleKeyInfo Key, TaskCompletionSource Handled);
    }
}
