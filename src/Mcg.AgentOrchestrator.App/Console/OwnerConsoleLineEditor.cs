using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleLineEditor(IOwnerConsoleKeySource keys, IOwnerConsoleTextSink sink)
{
    // The loop and session construct separate output adapters for the same physical console.
    // Share its reader and edit state; injected editors keep independent sessions isolated.
    private static readonly Lazy<OwnerConsoleLineEditor> SystemEditor = new(() =>
        new(new SystemConsoleKeySource(), new SystemConsoleTextSink()));
    internal static OwnerConsoleLineEditor System => SystemEditor.Value;

    private readonly object _gate = new();
    private readonly StringBuilder _line = new();
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleWriter = true,
        SingleReader = false,
        AllowSynchronousContinuations = false
    });
    private int _readerStarted;

    internal bool IsInputRedirected => keys.IsInputRedirected;
    internal bool IsEditingLine
    {
        get { lock (_gate) return _line.Length > 0; }
    }

    internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureReaderStarted();
        try
        {
            // Cancellation removes this waiter, never the session's buffer or key reader.
            return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException ex)
        {
            if (ex.InnerException is not null)
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            return null;
        }
    }

    internal void WriteOutput(string text, bool newLine)
    {
        lock (_gate)
        {
            // Only the bell is safe to pass through mid-line; newline/backspace controls move it.
            if (_line.Length == 0 || (!newLine && text.All(c => c == '\a')))
            {
                sink.Write(text + (newLine ? Environment.NewLine : ""));
                return;
            }

            sink.Write(Environment.NewLine);
            sink.Write(text);
            sink.Write(Environment.NewLine);
            sink.Write(_line.ToString());
        }
    }

    private void EnsureReaderStarted()
    {
        if (Interlocked.CompareExchange(ref _readerStarted, 1, 0) != 0) return;
        // Console.ReadKey cannot be cancelled. A background reader lives with the console,
        // rather than leaving a new blocked reader behind whenever a read is cancelled.
        new Thread(ReadKeys) { IsBackground = true, Name = "owner-console-keys" }.Start();
    }

    private void ReadKeys()
    {
        try
        {
            while (keys.ReadKey() is { } key)
            {
                lock (_gate)
                {
                    if (key.Key == ConsoleKey.Enter)
                    {
                        sink.Write(Environment.NewLine);
                        var completed = _line.ToString();
                        _line.Clear();
                        _lines.Writer.TryWrite(completed);
                    }
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        if (_line.Length > 0)
                        {
                            _line.Length--;
                            sink.Write("\b \b");
                        }
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        _line.Append(key.KeyChar);
                        sink.Write(key.KeyChar.ToString());
                    }
                }
            }
            _lines.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _lines.Writer.TryComplete(ex);
        }
    }
}
