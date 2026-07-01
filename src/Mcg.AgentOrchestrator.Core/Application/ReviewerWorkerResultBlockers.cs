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
        var inBlock = false;

        foreach (var rawLine in lines)
        {
            var line = NormalizeWorkerResultLine(rawLine);
            if (string.Equals(line, "WORKER_RESULT:", StringComparison.OrdinalIgnoreCase))
            {
                inBlock = true;
                continue;
            }

            if (string.Equals(line, "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase))
            {
                if (inBlock)
                {
                    yield break;
                }

                continue;
            }

            if (inBlock && line.Length > 0)
            {
                yield return line;
            }
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

        var key = line[..sep].Trim();
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
        var trimmed = line.Trim();
        while (trimmed.StartsWith('#'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        return trimmed.Trim('*', '_', '`', ' ');
    }
}
