using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Reads one redirected stream to EOF on a dedicated background thread so child output does not
// depend on thread-pool availability. Callers retain ownership of the reader and its process.
internal sealed class PipeDrain
{
    internal const int DefaultTimeoutMilliseconds = 5_000;

    private readonly TextReader _reader;
    private readonly StringBuilder _buffer = new();
    private readonly Thread _thread;
    private volatile bool _completed;
    private volatile string? _failure;

    private PipeDrain(TextReader reader, string name)
    {
        _reader = reader;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
    }

    internal static PipeDrain Start(TextReader reader, string name)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var drain = new PipeDrain(reader, name);
        drain._thread.Start();
        return drain;
    }

    internal string Text
    {
        get
        {
            lock (_buffer)
            {
                return _buffer.ToString();
            }
        }
    }

    internal bool Join(long deadlineTickCount)
    {
        var remaining = (int)Math.Clamp(deadlineTickCount - Environment.TickCount64, 0, int.MaxValue);
        return _thread.Join(remaining);
    }

    internal string Describe()
    {
        int chars;
        lock (_buffer)
        {
            chars = _buffer.Length;
        }

        var state = _completed ? "completed" : _thread.IsAlive ? "running" : "not-started";
        return $"{state}(chars={chars}{(_failure is null ? string.Empty : $", failure={_failure}")})";
    }

    internal static string DescribeTimeout(
        string operation,
        int timeoutMilliseconds,
        PipeDrain? stdoutDrain,
        PipeDrain? stderrDrain)
    {
        ThreadPool.GetMinThreads(out var minWorkerThreads, out _);
        ThreadPool.GetAvailableThreads(out var availableWorkerThreads, out _);
        return $"{operation} output drain timed out after {timeoutMilliseconds}ms with the child exited: " +
            $"stdoutDrain={stdoutDrain?.Describe() ?? "not-applicable"}; " +
            $"stderrDrain={stderrDrain?.Describe() ?? "not-applicable"}; " +
            $"poolThreads={ThreadPool.ThreadCount}; poolPendingWorkItems={ThreadPool.PendingWorkItemCount}; " +
            $"poolMinWorkers={minWorkerThreads}; poolAvailableWorkers={availableWorkerThreads}";
    }

    internal static string AppendDiagnostic(string text, string diagnostic) =>
        string.IsNullOrEmpty(text) ? diagnostic : text + Environment.NewLine + diagnostic;

    private void Run()
    {
        var chunk = new char[4096];
        try
        {
            int read;
            while ((read = _reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                lock (_buffer)
                {
                    _buffer.Append(chunk, 0, read);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _failure = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            _completed = true;
        }
    }
}
