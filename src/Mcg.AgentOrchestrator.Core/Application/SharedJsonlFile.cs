using System.Text;

namespace Mcg.AgentOrchestrator.Core;

public static class SharedJsonlFile
{
    internal const int MaximumShareViolationRetries = 5;
    private const int RetryDelayMilliseconds = 25;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string[] ReadAllLines(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8WithoutBom, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    public static void AppendLine(string path, string line) =>
        AppendLine(path, line, retryObserver: null);

    public static void AppendLines(string path, IEnumerable<string> lines) =>
        AppendLines(path, lines, retryObserver: null);

    internal static void AppendLine(string path, string line, Action<int>? retryObserver) =>
        AppendPayload(path, line + Environment.NewLine, retryObserver);

    internal static void AppendLines(string path, IEnumerable<string> lines, Action<int>? retryObserver)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var materializedLines = lines as string[] ?? lines.ToArray();
        if (materializedLines.Length == 0)
        {
            return;
        }

        var payload = string.Join(Environment.NewLine, materializedLines);
        AppendPayload(path, payload + Environment.NewLine, retryObserver);
    }

    private static void AppendPayload(string path, string payload, Action<int>? retryObserver)
    {
        var bytes = Utf8WithoutBom.GetBytes(payload);
        using var stream = OpenAppendStream(path, retryObserver);
        stream.Write(bytes);
    }

    private static FileStream OpenAppendStream(string path, Action<int>? retryObserver)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 1);
            }
            catch (IOException ex) when (IsShareViolation(ex) && retry < MaximumShareViolationRetries)
            {
                var retryNumber = retry + 1;
                retryObserver?.Invoke(retryNumber);
                Thread.Sleep(RetryDelayMilliseconds * retryNumber);
            }
        }
    }

    private static bool IsShareViolation(IOException exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is ErrorSharingViolation or ErrorLockViolation;
    }
}
