using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class ProcessLogReader
{
    private readonly Func<string, Stream> _openLogReadStream;
    private readonly Dictionary<ProcessDecisionLogCacheKey, ProcessDecisionLogSnapshot> _decisionLogCache = [];
    private readonly object _decisionLogCacheGate = new();

    internal ProcessLogReader(Func<string, Stream>? openLogReadStream = null)
    {
        _openLogReadStream = openLogReadStream ??
            (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    }

    internal ProcessDecisionLogSnapshot ReadBestEffort(TaskProcessRecord processRecord, string path)
    {
        var key = new ProcessDecisionLogCacheKey(processRecord.ExitCodePath, path);
        var length = SafeFileLength(path);
        lock (_decisionLogCacheGate)
        {
            if (_decisionLogCache.TryGetValue(key, out var cached) && cached.Length == length)
            {
                return cached;
            }
        }

        var snapshot = ReadFileBestEffort(path, length);
        if (snapshot.ReadSucceeded)
        {
            lock (_decisionLogCacheGate)
            {
                _decisionLogCache[key] = snapshot;
            }
        }

        return snapshot;
    }

    internal CompleteLogReadResult ReadComplete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new CompleteLogReadResult(null, "missing");
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return new CompleteLogReadResult(reader.ReadToEnd(), null);
        }
        catch (IOException)
        {
            return new CompleteLogReadResult(null, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new CompleteLogReadResult(null, "unreadable");
        }
    }

    internal static string NormalizeCompleteDecisionText(string content)
    {
        var hasBareCarriageReturn = false;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r' && (index + 1 == content.Length || content[index + 1] != '\n'))
            {
                hasBareCarriageReturn = true;
                break;
            }
        }

        if (!hasBareCarriageReturn)
        {
            return content;
        }

        var normalized = new StringBuilder(content.Length);
        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (ch == '\r' && (index + 1 == content.Length || content[index + 1] != '\n'))
            {
                normalized.Append(Environment.NewLine);
            }
            else
            {
                normalized.Append(ch);
            }
        }

        return normalized.ToString();
    }

    internal void Evict(TaskProcessRecord processRecord)
    {
        lock (_decisionLogCacheGate)
        {
            _decisionLogCache.Remove(new ProcessDecisionLogCacheKey(processRecord.ExitCodePath, processRecord.StandardOutputPath));
            _decisionLogCache.Remove(new ProcessDecisionLogCacheKey(processRecord.ExitCodePath, processRecord.StandardErrorPath));
        }
    }

    internal static string ReadDecisionBestEffort(string path)
    {
        var reader = new ProcessLogReader();
        return reader.ReadFileBestEffort(path, SafeFileLength(path)).DecisionText;
    }

    internal static string ReadBoundedBestEffort(string path)
    {
        var reader = new ProcessLogReader();
        return reader.ReadFileBestEffort(path, SafeFileLength(path)).BoundedText;
    }

    internal static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private ProcessDecisionLogSnapshot ReadFileBestEffort(string path, long length)
    {
        if (!File.Exists(path))
        {
            return new ProcessDecisionLogSnapshot(length, false, string.Empty, string.Empty, ReadSucceeded: true);
        }

        try
        {
            using var stream = _openLogReadStream(path);
            using var reader = new StreamReader(stream);
            return ReadSnapshot(reader, path, length);
        }
        catch (IOException)
        {
            var message = $"[log locked at refresh — see {path}]";
            return new ProcessDecisionLogSnapshot(length, false, message, message, ReadSucceeded: false);
        }
        catch (UnauthorizedAccessException)
        {
            var message = $"[log unreadable at refresh — see {path}]";
            return new ProcessDecisionLogSnapshot(length, false, message, message, ReadSucceeded: false);
        }
    }

    private static ProcessDecisionLogSnapshot ReadSnapshot(TextReader reader, string path, long length)
    {
        const int MaxDecisionChars = VerificationTextBounds.MaxRetainedChars;
        var decision = new StringBuilder(Math.Min(MaxDecisionChars, VerificationTextBounds.BoundThreshold));
        var workerResult = new StringBuilder(Math.Min(MaxDecisionChars, VerificationTextBounds.BoundThreshold));
        var prefixRemaining = VerificationTextBounds.PreviewHeadChars;
        var inWorkerResult = false;
        var decisionTruncatedChars = 0L;
        var lineBuffer = new StringBuilder();
        var retainedPrefix = new StringBuilder(VerificationTextBounds.BoundThreshold);
        var tail = new char[VerificationTextBounds.PreviewTailChars];
        var tailStart = 0;
        var tailCount = 0;
        var totalChars = 0L;
        var buffer = new char[4096];
        var previousWasCarriageReturn = false;

        void ProcessDecisionLine()
        {
            var line = lineBuffer.ToString();
            lineBuffer.Clear();
            var containsFinalOutput = ContainsCodexFinalOutput(line);
            var normalized = NormalizeWorkerResultMarker(line);
            var isOpener = IsWorkerResultOpener(normalized);
            var isEndMarker = !isOpener && IsWorkerResultEndMarker(normalized);
            var isWorkerResultLine = isOpener || inWorkerResult || isEndMarker;
            var isDecisionContent =
                isOpener || isEndMarker || inWorkerResult || IsDecisionSignificantLine(line, containsFinalOutput);

            if (prefixRemaining > 0)
            {
                var take = Math.Min(prefixRemaining, line.Length);
                if (!isDecisionContent)
                {
                    decisionTruncatedChars += AppendDecisionLine(decision, line[..take], MaxDecisionChars);
                }

                prefixRemaining -= take;
            }

            if (isOpener)
            {
                inWorkerResult = true;
            }

            if (isDecisionContent)
            {
                decisionTruncatedChars += AppendDecisionLine(
                    isWorkerResultLine ? workerResult : decision,
                    line,
                    MaxDecisionChars);
            }

            if (isEndMarker)
            {
                inWorkerResult = false;
            }
        }

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (retainedPrefix.Length < VerificationTextBounds.BoundThreshold)
            {
                retainedPrefix.Append(buffer, 0, Math.Min(read, VerificationTextBounds.BoundThreshold - retainedPrefix.Length));
            }

            for (var index = 0; index < read; index++)
            {
                var ch = buffer[index];
                if (tailCount < tail.Length)
                {
                    tail[(tailStart + tailCount) % tail.Length] = ch;
                    tailCount++;
                }
                else
                {
                    tail[tailStart] = ch;
                    tailStart = (tailStart + 1) % tail.Length;
                }

                if (ch == '\r')
                {
                    ProcessDecisionLine();
                    previousWasCarriageReturn = true;
                }
                else if (ch == '\n')
                {
                    if (!previousWasCarriageReturn)
                    {
                        ProcessDecisionLine();
                    }

                    previousWasCarriageReturn = false;
                }
                else
                {
                    lineBuffer.Append(ch);
                    previousWasCarriageReturn = false;
                }
            }

            totalChars += read;
        }

        if (lineBuffer.Length > 0)
        {
            ProcessDecisionLine();
        }

        var bounded = BuildBoundedText(retainedPrefix, tail, tailStart, tailCount, totalChars, path);
        var decisionProse = decision.ToString().TrimEnd();
        var workerResultText = workerResult.ToString().TrimEnd();
        string decisionText;
        if (workerResultText.Length == 0)
        {
            decisionText = decisionProse;
        }
        else
        {
            // WORKER_RESULT is contractual; surrounding prose is not. Preserve the captured block in the
            // same fixed-size snapshot and evict prose first. Raising the cap would only move the cliff;
            // completed-result decisions use the complete stdout artifact as their authority.
            var separatorLength = decisionProse.Length > 0 ? Environment.NewLine.Length : 0;
            var retainedProseLength = Math.Max(0, Math.Min(
                decisionProse.Length,
                MaxDecisionChars - workerResultText.Length - separatorLength));
            decisionTruncatedChars += decisionProse.Length - retainedProseLength;
            var retainedProse = decisionProse[..retainedProseLength].TrimEnd();
            decisionText = retainedProse.Length == 0
                ? workerResultText
                : retainedProse + Environment.NewLine + workerResultText;
        }

        return new ProcessDecisionLogSnapshot(
            length,
            ContainsCodexFinalOutput(decisionText),
            decisionText,
            bounded,
            decisionTruncatedChars);
    }

    private static string BuildBoundedText(
        StringBuilder retainedPrefix,
        char[] tail,
        int tailStart,
        int tailCount,
        long totalChars,
        string path)
    {
        if (totalChars <= VerificationTextBounds.BoundThreshold)
        {
            return retainedPrefix.ToString();
        }

        var head = retainedPrefix.ToString(0, VerificationTextBounds.PreviewHeadChars);
        var boundedTail = BuildTailText(tail, tailStart, tailCount);
        return VerificationTextBounds.BuildBoundedText(head, boundedTail, totalChars, path);
    }

    private static long AppendDecisionLine(StringBuilder target, string line, int maxChars)
    {
        if (string.IsNullOrEmpty(line))
        {
            return 0;
        }

        var attemptedChars = (long)line.Length + (target.Length > 0 ? Environment.NewLine.Length : 0);
        if (target.Length >= maxChars)
        {
            return attemptedChars;
        }

        if (target.Length > 0)
        {
            if (target.Length + Environment.NewLine.Length >= maxChars)
            {
                return attemptedChars;
            }

            target.AppendLine();
        }

        var remaining = maxChars - target.Length;
        var appendedChars = Math.Min(line.Length, remaining);
        target.Append(line, 0, appendedChars);
        return line.Length - appendedChars;
    }

    private static bool IsDecisionSignificantLine(string line, bool containsCodexFinalOutput)
    {
        return DispatchFailureClassifier.HasVerificationEvidenceInOutput(line, string.Empty) ||
            line.Contains("PLANNER_EVIDENCE_REQUEST:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("HUMAN_INPUT:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("HUMAN INPUT:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("NO_CHANGE:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No-change rationale:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No changes needed:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("CreateProcessAsUserW", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("1312", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("specified logon session does not exist", StringComparison.OrdinalIgnoreCase) ||
            (line.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
             line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) ||
            line.Contains("sandbox-prep", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[dispatch-host] terminating worker tree:", StringComparison.Ordinal) ||
            containsCodexFinalOutput ||
            IsProviderDecisionLine(line) ||
            line.Contains("Model fit:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Changed files:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Files changed:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderDecisionLine(string line)
    {
        return line.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("websocket", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("access token", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid model", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("400", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkerResultOpener(string normalizedLine) =>
        string.Equals(normalizedLine.TrimEnd(':').Trim(), "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkerResultEndMarker(string normalizedLine) =>
        string.Equals(normalizedLine.Trim(), "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWorkerResultMarker(string text)
    {
        var trimmed = text.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var ch in trimmed)
        {
            if (ch is not ('#' or '*' or '`'))
            {
                buffer[length++] = ch;
            }
        }

        return new string(buffer, 0, length).Trim();
    }

    private static string BuildTailText(char[] tail, int tailStart, int tailCount)
    {
        if (tailCount == 0)
        {
            return string.Empty;
        }

        if (tailStart + tailCount <= tail.Length)
        {
            return new string(tail, tailStart, tailCount);
        }

        var suffixLength = tail.Length - tailStart;
        var builder = new StringBuilder(tailCount);
        builder.Append(tail, tailStart, suffixLength);
        builder.Append(tail, 0, tailCount - suffixLength);
        return builder.ToString();
    }

    private static bool ContainsCodexFinalOutput(string value)
    {
        return value.Contains("tokens used", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token usage", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProcessDecisionLogCacheKey(string ProcessRecordKey, string Path);
}

internal sealed record ProcessDecisionLogSnapshot(
    long Length,
    bool FinalOutputSeen,
    string DecisionText,
    string BoundedText,
    long DecisionTruncatedChars = 0,
    bool ReadSucceeded = true);

internal sealed record CompleteLogReadResult(string? Content, string? UnavailableReason);
