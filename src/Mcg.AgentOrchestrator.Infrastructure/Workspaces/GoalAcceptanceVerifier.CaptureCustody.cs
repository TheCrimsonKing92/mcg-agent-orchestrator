using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using CaptureLimitResult = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.CaptureLimitResult;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalAcceptanceVerifierCaptureCustody
{
    private static string BuildShellCommand(string[] arguments, Func<string, string> quote) =>
        string.Join(' ', arguments.Select(quote));

    private static string QuoteForCmd(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteForPosix(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    internal static async Task<string> ReadFileWithRetryAsync(string path, int? maximumBytes = null)
    {
        // A reparented grandchild may still hold the file's write handle; open shared and tolerate
        // transient locks. The output we need (the child's own writes) is already flushed on exit.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                if (maximumBytes is { } limit && stream.Length > limit)
                {
                    const string spliceMarker = "\n[... captured output omitted ...]\n";
                    var markerBytes = Encoding.UTF8.GetByteCount(spliceMarker);
                    var contentBytes = Math.Max(2, limit - markerBytes);
                    var headBytes = Math.Max(1, contentBytes * 3 / 4);
                    var tailBytes = Math.Max(1, contentBytes - headBytes);
                    using var head = new MemoryStream();
                    using var tail = new MemoryStream();
                    await CopyAtMostAsync(stream, head, headBytes).ConfigureAwait(false);
                    stream.Seek(-Math.Min(tailBytes, stream.Length), SeekOrigin.End);
                    await CopyAtMostAsync(stream, tail, tailBytes).ConfigureAwait(false);
                    return DecodeCapturedWindow(head.GetBuffer().AsSpan(0, checked((int)head.Length)), trimLeading: false) +
                        spliceMarker +
                        DecodeCapturedWindow(tail.GetBuffer().AsSpan(0, checked((int)tail.Length)), trimLeading: true);
                }
                else
                {
                    await stream.CopyToAsync(buffer).ConfigureAwait(false);
                }
                return DecodeCapturedOutput(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
            }
            catch (FileNotFoundException)
            {
                return string.Empty;
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        return string.Empty;
    }

    internal static Task<string> ReadCapturedFileWithRetryAsync(string path, bool captureLimitReached) =>
        ReadFileWithRetryAsync(path, captureLimitReached ? GoalAcceptanceVerifier.CappedOutputPreviewBytes : null);

    internal static ProcessStartInfo BuildAcceptanceProcessStartInfo(
        string[] arguments,
        string workingDirectory,
        string? stdoutRedirectTarget = null,
        string? stderrRedirectTarget = null,
        bool forceUtf8ConsoleOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        if (OperatingSystem.IsWindows())
        {
            if (string.IsNullOrWhiteSpace(stdoutRedirectTarget) != string.IsNullOrWhiteSpace(stderrRedirectTarget))
            {
                throw new ArgumentException("Both capture redirection targets must be provided together.");
            }

            startInfo.FileName = "cmd.exe";
            var command = BuildShellCommand(arguments, QuoteForCmd);
            if (forceUtf8ConsoleOutput)
            {
                // MTP formats theory arguments before writing them. Under an OEM console code page,
                // Windows best-fit conversion irreversibly changes CJK and combining characters before
                // capture, so decoding the resulting bytes cannot repair the discovery identity.
                // Group the preflight with the child command so owned stdout/stderr redirections
                // are opened before chcp runs. A failed preflight then returns its non-zero exit
                // code and captured stderr instead of leaving the named-pipe readers unconnected.
                command = $"(chcp 65001 > nul && {command})";
            }
            if (!string.IsNullOrWhiteSpace(stdoutRedirectTarget))
            {
                command = $"{command} > {QuoteForCmd(stdoutRedirectTarget)} 2> {QuoteForCmd(stderrRedirectTarget!)}";
            }
            else
            {
                // Retain the managed-pipe form for focused capture tests and non-owned callers.
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            // cmd /c strips one surrounding quote pair, so wrap the whole command once.
            startInfo.Arguments = $"/c \"{command}\"";
            GoalAcceptanceVerifier.ThrowIfCmdCommandLineTooLong(startInfo.Arguments, arguments);
        }
        else
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildShellCommand(arguments, QuoteForPosix));
        }

        return startInfo;
    }

    internal static NamedPipeServerStream CreateCapturePipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    internal static async Task<CaptureLimitResult> ConnectAndDrainCappedCaptureAsync(
        NamedPipeServerStream source,
        Task connection,
        string path,
        long limitBytes,
        Func<DateTimeOffset> utcNow,
        Action? onLimitReached, CancellationToken cancellationToken, TimeSpan? capturePublicationInterval)
    {
        await connection.ConfigureAwait(false);
        return await GoalAcceptanceVerifier.DrainCappedCaptureAsync(
            source,
            path,
            limitBytes,
            utcNow,
            onLimitReached,
            cancellationToken, capturePublicationInterval).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<CaptureLimitResult>> CompleteCaptureDrainsAsync(
        RegisteredOwnedProcess process,
        Task<CaptureLimitResult>[] captureDrains,
        CancellationTokenSource captureDrainCts,
        IReadOnlyList<Stream> captureSources)
    {
        try
        {
            return await Task.WhenAll(captureDrains)
                .WaitAsync(GoalAcceptanceVerifier.CaptureDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await captureDrainCts.CancelAsync().ConfigureAwait(false);
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            DisposeCaptureSources(captureSources);
            return await Task.WhenAll(captureDrains)
                .WaitAsync(GoalAcceptanceVerifier.CaptureDrainTimeout)
                .ConfigureAwait(false);
        }
    }

    internal static async Task CancelCaptureDrainsAsync(
        CancellationTokenSource captureDrainCts,
        Task<CaptureLimitResult>[]? captureDrains,
        IReadOnlyList<Stream>? captureSources)
    {
        try { await captureDrainCts.CancelAsync().ConfigureAwait(false); } catch { }
        DisposeCaptureSources(captureSources);
        if (captureDrains is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(captureDrains)
                .WaitAsync(GoalAcceptanceVerifier.CaptureDrainTimeout)
                .ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best effort, but it is always time-bounded. A descendant can retain a
            // copied pipe handle even after process-tree termination fails.
        }
    }

    internal static void DisposeCaptureSources(IReadOnlyList<Stream>? captureSources)
    {
        if (captureSources is null)
        {
            return;
        }

        foreach (var source in captureSources)
        {
            try { source.Dispose(); } catch { }
        }
    }

    internal static bool IsClosedPipe(IOException exception)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var nativeErrorCode = exception.HResult & 0xffff;
        return nativeErrorCode is 109 or 232 or 233; // broken pipe, no data, pipe not connected
    }

    internal static async Task FinalizeCappedCaptureAsync(
        string path,
        long limitBytes,
        long writtenBytes,
        DateTimeOffset timestamp)
    {
        var terminator = Encoding.UTF8.GetBytes(
            $"\n[ACCEPTANCE_CAPTURE_LIMIT_REACHED cap_bytes={limitBytes} written_bytes={writtenBytes} timestamp={timestamp:O}]\n");
        var reserved = Math.Min(terminator.Length, checked((int)Math.Min(limitBytes, int.MaxValue)));
        await using var destination = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            useAsync: true);
        destination.SetLength(Math.Max(0, limitBytes - reserved));
        destination.Position = destination.Length;
        await destination.WriteAsync(terminator.AsMemory(terminator.Length - reserved, reserved)).ConfigureAwait(false);
        await destination.FlushAsync().ConfigureAwait(false);
    }

    private static async Task CopyAtMostAsync(Stream source, Stream destination, int maximumBytes)
    {
        var buffer = new byte[Math.Min(16 * 1024, maximumBytes)];
        var remaining = maximumBytes;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (read == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static string DecodeCapturedWindow(ReadOnlySpan<byte> bytes, bool trimLeading)
    {
        if (trimLeading)
        {
            while (!bytes.IsEmpty && (bytes[0] & 0xc0) == 0x80)
                bytes = bytes[1..];
        }

        // A byte window may end in the middle of a UTF-8 sequence. Remove only the incomplete
        // suffix; complete non-UTF8 data still follows the existing OEM/Latin-1 fallback below.
        for (var trim = 0; trim < Math.Min(3, bytes.Length); trim++)
        {
            try
            {
                return GoalAcceptanceVerifier.StrictUtf8.GetString(bytes[..(bytes.Length - trim)]);
            }
            catch (DecoderFallbackException)
            {
                // Try one fewer trailing byte before falling back to the established decoder.
            }
        }

        return DecodeCapturedOutput(bytes);
    }

    internal static void EmitCaptureLimitReached(
        string? goalId,
        string? attemptPrefix,
        string path,
        long capBytes,
        TextWriter writer)
    {
        var runId = Path.GetFileName(
            attemptPrefix ?? Path.GetFileNameWithoutExtension(path));
        writer.WriteLine(
            $"ACCEPTANCE_CAPTURE_LIMIT_REACHED goal={GoalAcceptanceVerifier.FormatNullableToken(goalId, 8)} run={GoalAcceptanceVerifier.QuoteProgressToken(runId)} path={GoalAcceptanceVerifier.QuoteProgressToken(path)} cap_bytes={capBytes}");
        writer.Flush();
    }

    internal static string DecodeCapturedOutput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            return GoalAcceptanceVerifier.StrictUtf8.GetString(bytes[Encoding.UTF8.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.UTF32.Preamble))
        {
            return GoalAcceptanceVerifier.StrictUtf32LittleEndian.GetString(bytes[Encoding.UTF32.Preamble.Length..]);
        }

        if (bytes.StartsWith(GoalAcceptanceVerifier.StrictUtf32BigEndian.Preamble))
        {
            return GoalAcceptanceVerifier.StrictUtf32BigEndian.GetString(bytes[GoalAcceptanceVerifier.StrictUtf32BigEndian.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.Unicode.Preamble))
        {
            return GoalAcceptanceVerifier.StrictUtf16LittleEndian.GetString(bytes[Encoding.Unicode.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return GoalAcceptanceVerifier.StrictUtf16BigEndian.GetString(bytes[Encoding.BigEndianUnicode.Preamble.Length..]);
        }

        try
        {
            return GoalAcceptanceVerifier.StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException) when (OperatingSystem.IsWindows())
        {
            return DecodeWindowsOem(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string DecodeWindowsOem(ReadOnlySpan<byte> bytes)
    {
        const uint CpOem = 1;
        var source = bytes.ToArray();
        var charCount = MultiByteToWideChar(CpOem, 0, source, source.Length, null, 0);
        if (charCount <= 0)
        {
            throw new InvalidOperationException(
                $"Unable to decode captured output with the Windows OEM code page. Win32Error={Marshal.GetLastWin32Error()}.");
        }

        var chars = new char[charCount];
        var converted = MultiByteToWideChar(CpOem, 0, source, source.Length, chars, chars.Length);
        if (converted != charCount)
        {
            throw new InvalidOperationException(
                $"Unable to decode captured output with the Windows OEM code page. Win32Error={Marshal.GetLastWin32Error()}.");
        }

        return new string(chars);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte[] multiByteText,
        int byteCount,
        [Out] char[]? wideText,
        int charCount);
}
