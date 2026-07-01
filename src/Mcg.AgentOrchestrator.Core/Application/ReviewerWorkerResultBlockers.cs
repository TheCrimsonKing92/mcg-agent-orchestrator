namespace Mcg.AgentOrchestrator.Core;

internal static class ReviewerWorkerResultBlockers
{
    public static bool TryFindBlocker(TaskVerificationRecord? verification, out string blocker)
    {
        blocker = string.Empty;
        if (verification is null)
        {
            return false;
        }

        foreach (var line in EnumerateWorkerResultLines(verification))
        {
            if (TryFindBlockersField(line, out blocker) ||
                TryFindBlockerMarkedFinding(line, out blocker))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateWorkerResultLines(TaskVerificationRecord verification)
    {
        var combined = $"{verification.StandardOutput}\n{verification.StandardError}";
        var lines = combined.Replace("\r\n", "\n").Split('\n');
        List<string>? latestBlock = null;
        var currentBlock = new List<string>();
        var inBlock = false;

        foreach (var rawLine in lines)
        {
            var line = NormalizeWorkerResultLine(rawLine);
            if (IsWorkerResultOpener(line))
            {
                inBlock = true;
                currentBlock.Clear();
                continue;
            }

            if (IsWorkerResultEndMarker(line))
            {
                if (inBlock)
                {
                    latestBlock = [.. currentBlock];
                    currentBlock.Clear();
                    inBlock = false;
                }

                continue;
            }

            if (inBlock && line.Length > 0)
            {
                currentBlock.Add(line);
            }
        }

        if (inBlock)
        {
            latestBlock = [.. currentBlock];
        }

        if (latestBlock is null)
        {
            yield break;
        }

        foreach (var line in latestBlock)
        {
            yield return line;
        }
    }

    private static bool TryFindBlockersField(string line, out string blocker)
    {
        blocker = string.Empty;
        var sep = line.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0)
        {
            return false;
        }

        var key = NormalizeWorkerResultKey(line[..sep]);
        if (!string.Equals(key, "blockers", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = line[(sep + 1)..].Trim();
        if (IsNoBlockerValue(value))
        {
            return false;
        }

        blocker = value;
        return true;
    }

    private static bool TryFindBlockerMarkedFinding(string line, out string blocker)
    {
        blocker = string.Empty;
        if (!line.Contains("blocker", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ContainsBlockerClassification(line) || line.StartsWith("blocker:", StringComparison.OrdinalIgnoreCase))
        {
            blocker = line;
            return true;
        }

        return false;
    }

    private static bool ContainsBlockerClassification(string line)
    {
        var normalized = line
            .Replace("\"", "", StringComparison.Ordinal)
            .Replace("'", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal)
            .Trim();

        return normalized.Contains("severity: blocker", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("severity=blocker", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("type: blocker", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("type=blocker", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("kind: blocker", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("kind=blocker", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("isblocker: true", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("isblocker=true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNoBlockerValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.StartsWith('<') && value.EndsWith('>'))
        {
            return true;
        }

        return value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none ", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none-", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none.", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("none:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWorkerResultLine(string line)
    {
        return line.Trim();
    }

    private static bool IsWorkerResultOpener(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line).TrimEnd(':').Trim();
        return string.Equals(normalized, "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkerResultEndMarker(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line);
        return string.Equals(normalized, "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWorkerResultKey(string key)
    {
        return NormalizeWorkerResultMarker(key).TrimStart('-', ' ').Trim();
    }

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
}
