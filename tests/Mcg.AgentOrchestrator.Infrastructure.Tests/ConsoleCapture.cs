using System.Runtime.CompilerServices;
using System.Text;

// Async-context-local console router: installed once via [ModuleInitializer] so that
// Console.Out is never reassigned per-test. CaptureConsole sets AsyncLocal<TextWriter?>
// for the duration of an action; parallel tests each get their own isolated capture.
internal sealed class AsyncLocalConsoleRouter : TextWriter
{
    private static readonly AsyncLocal<TextWriter?> _current = new();
    private TextWriter _fallback = Null;

    internal static readonly AsyncLocalConsoleRouter Instance = new();

    internal void SetFallback(TextWriter fallback) => _fallback = fallback;

    public override Encoding Encoding => Active.Encoding;

    private TextWriter Active => _current.Value ?? _fallback;

    public override void Write(char value) => Active.Write(value);
    public override void Write(string? value) => Active.Write(value);
    public override void WriteLine(string? value) => Active.WriteLine(value);
    public override void WriteLine() => Active.WriteLine();
    public override void Write(char[] buffer, int index, int count) => Active.Write(buffer, index, count);
    public override void Write(ReadOnlySpan<char> buffer) => Active.Write(buffer);
    public override void WriteLine(ReadOnlySpan<char> buffer) => Active.WriteLine(buffer);
    public override void Flush() => Active.Flush();
    public override Task FlushAsync() => Active.FlushAsync();
    public override Task WriteAsync(char value) => Active.WriteAsync(value);
    public override Task WriteAsync(string? value) => Active.WriteAsync(value);
    public override Task WriteLineAsync(string? value) => Active.WriteLineAsync(value);
    public override Task WriteLineAsync() => Active.WriteLineAsync();

    internal static string Capture(Action action)
    {
        var writer = new StringWriter();
        var previous = _current.Value;
        _current.Value = writer;
        try
        {
            action();
        }
        finally
        {
            _current.Value = previous;
        }
        return writer.ToString();
    }
}

internal static class ConsoleCaptureInitializer
{
    [ModuleInitializer]
    internal static void Install()
    {
        AsyncLocalConsoleRouter.Instance.SetFallback(Console.Out);
        Console.SetOut(AsyncLocalConsoleRouter.Instance);
    }
}
