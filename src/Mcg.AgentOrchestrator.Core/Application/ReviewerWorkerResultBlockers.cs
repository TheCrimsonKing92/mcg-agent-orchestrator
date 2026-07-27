using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static class WorkerResultBlockers
{
    public enum TestsStatus
    {
        Unknown,
        Pass,
        Fail,
        NotRun,
        Deferred
    }

    public enum BlockersStatus
    {
        Unknown,
        None,
        Present
    }

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

    public static bool TryFindNeedsWorkVerdict(TaskVerificationRecord? verification, out string blocker)
    {
        blocker = string.Empty;
        if (verification is null)
        {
            return false;
        }

        var hasNeedsWorkVerdict = false;
        foreach (var line in EnumerateWorkerResultLines(verification))
        {
            if (TryFindField(line, "verdict", out var verdict) &&
                IsNeedsWorkVerdict(verdict))
            {
                hasNeedsWorkVerdict = true;
            }

            if (TryFindBlockersField(line, out var candidate) ||
                TryFindBlockerMarkedFinding(line, out candidate))
            {
                blocker = candidate;
            }
        }

        if (!hasNeedsWorkVerdict)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(blocker))
        {
            return true;
        }

        if (!TryFindReviewFindingRound(verification, out var round, out _))
        {
            return false;
        }

        blocker = string.Join(
            "; ",
            round.Findings
                .Where(finding => finding.State == ReviewFindingState.Open)
                .Select(finding => finding.Description));
        return !string.IsNullOrWhiteSpace(blocker);
    }

    public static bool TryFindUnsuppressedNeedsWorkVerdict(
        TaskVerificationRecord? verification,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections,
        out string blocker,
        out IReadOnlyList<string> suppressedFindings)
    {
        suppressedFindings = [];
        if (!TryFindNeedsWorkVerdict(verification, out blocker))
        {
            return false;
        }

        if (criteriaCorrections.Count == 0)
        {
            return true;
        }

        ReviewFindings.TryFilterWaivedDescriptions(
            SplitBlockerFindings(blocker),
            criteriaCorrections,
            out var kept,
            out var suppressed);
        if (suppressed.Count == 0)
        {
            return true;
        }

        blocker = string.Join("; ", kept);
        suppressedFindings = suppressed.Select(item => item.Finding).ToArray();
        return !string.IsNullOrWhiteSpace(blocker);
    }

    public static bool IsSuppressedByCriteriaCorrection(
        string finding,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections) =>
        ReviewFindings.IsWaived(finding, criteriaCorrections);

    public static bool TryFindEvidenceRequest(TaskVerificationRecord? verification, out string request)
    {
        request = string.Empty;
        if (verification is null)
        {
            return false;
        }

        foreach (var line in EnumerateWorkerResultLines(verification))
        {
            if (TryFindField(line, "evidence-request", out var value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !IsNoBlockerValue(value))
            {
                request = value;
                return true;
            }
        }

        return false;
    }

    public static bool TryFindReviewFindingRound(
        TaskVerificationRecord? verification,
        out ReviewFindingRound round,
        out string diagnostic)
    {
        round = new ReviewFindingRound([], []);
        diagnostic = string.Empty;
        if (verification is null)
        {
            diagnostic = "reviewer verification is missing.";
            return false;
        }

        string? findingsJson = null;
        string? touchedAnchorsJson = null;
        foreach (var line in EnumerateWorkerResultLines(verification))
        {
            if (TryFindField(line, "findings", out var findings))
            {
                findingsJson = findings;
            }
            else if (TryFindField(line, "touched_anchors", out var touchedAnchors))
            {
                touchedAnchorsJson = touchedAnchors;
            }
        }

        if (string.IsNullOrWhiteSpace(findingsJson) || string.IsNullOrWhiteSpace(touchedAnchorsJson))
        {
            diagnostic = "reviewer WORKER_RESULT must contain one-line findings and touched_anchors JSON arrays.";
            return false;
        }

        if (!ReviewFindingConvergence.TryParseJson(findingsJson, touchedAnchorsJson, out var reportedRound, out diagnostic))
        {
            return false;
        }

        round = reportedRound with
        {
            // Reviewer-authored touched_anchors remains a required, validated receipt, but is not
            // authoritative for regression reopening. Only dispatch-preparation's round diff may
            // prove that a resolved structural anchor was touched.
            TouchedAnchors = verification.ReviewFindingTouchedAnchors ?? []
        };
        return true;
    }

    public static bool TryFindPassVerdict(TaskVerificationRecord? verification)
    {
        if (verification is null)
        {
            return false;
        }

        foreach (var line in EnumerateWorkerResultLines(verification))
        {
            if (TryFindField(line, "verdict", out var verdict) &&
                verdict.Trim().Equals("pass", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryFindHardFailureBlocker(TaskVerificationRecord? verification, out string blocker)
    {
        blocker = string.Empty;
        if (verification is null ||
            !TryFindBlocker(verification, out blocker))
        {
            return false;
        }

        if (!verification.Succeeded || !verification.HasCommittedChanges)
        {
            return true;
        }

        return TryFindFailingTests(verification, out _);
    }

    public static bool TryFindAdvisoryBlocker(TaskVerificationRecord? verification, out string blocker)
    {
        blocker = string.Empty;
        return verification is not null &&
            TryFindBlocker(verification, out blocker) &&
            !TryFindHardFailureBlocker(verification, out _);
    }

    public static bool IsAdvisoryNoChangeContractBlocker(TaskSpec task, TaskVerificationRecord? verification)
    {
        return verification is { Succeeded: true } &&
            IsNoChangeContractRole(task) &&
            TryFindBlocker(verification, out _) &&
            !TryFindFailingTests(verification, out _);
    }

    public static bool TryFindFailingTests(TaskVerificationRecord? verification, out string tests)
    {
        if (!TryFindTests(verification, out tests))
        {
            tests = string.Empty;
            return false;
        }

        if (TryParseTestsStatus(tests, out var status))
        {
            if (status == TestsStatus.Fail)
            {
                return true;
            }

            tests = string.Empty;
            return false;
        }

        if (TestsReportFailure(tests))
        {
            return true;
        }

        tests = string.Empty;
        return false;
    }

    public static bool TryGetTestsStatus(TaskVerificationRecord? verification, out TestsStatus status)
    {
        status = TestsStatus.Unknown;
        return TryFindTests(verification, out var tests) &&
            TryParseTestsStatus(tests, out status);
    }

    public static bool TryGetBlockersStatus(TaskVerificationRecord? verification, out BlockersStatus status)
    {
        status = BlockersStatus.Unknown;
        if (verification is null)
        {
            return false;
        }

        foreach (var line in EnumerateWorkerResultLines(verification))
        {
            if (TryFindField(line, "blockers", out var value) &&
                TryParseBlockersStatus(value, out status))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryFindTests(TaskVerificationRecord? verification, out string tests)
    {
        tests = string.Empty;
        if (verification is null)
        {
            return false;
        }

        foreach (var line in EnumerateWorkerResultLines(verification))
        {
            if (TryFindField(line, "tests", out tests))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateWorkerResultLines(TaskVerificationRecord verification)
    {
        List<string>? latestBlock = null;
        var currentBlock = new List<string>();
        var inBlock = false;

        foreach (var rawLine in EnumerateEvidenceLines(verification))
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
        if (!TryFindField(line, "blockers", out var value))
        {
            return false;
        }

        if (IsNoBlockerValue(value))
        {
            return false;
        }

        blocker = value;
        return true;
    }

    private static IReadOnlyList<string> SplitBlockerFindings(string blocker)
    {
        var findings = blocker
            .Replace("\r\n", "\n")
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.TrimStart('-', '*', ' '))
            .Where(item => item.Length > 0)
            .ToList();

        return findings.Count == 0 ? [blocker.Trim()] : findings;
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

    private static bool TryFindField(string line, string fieldName, out string value)
    {
        value = string.Empty;
        var sep = line.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0)
        {
            return false;
        }

        var key = NormalizeWorkerResultKey(line[..sep]);
        if (!string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = line[(sep + 1)..].Trim();
        return true;
    }

    private static bool TestsReportFailure(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (!normalized.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !normalized.Contains("failed: 0", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("failures: 0", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("0 failed", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("0 failures", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("no failures", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseTestsStatus(string value, out TestsStatus status)
    {
        status = ReadLeadingWorkerResultToken(value) switch
        {
            "pass" => TestsStatus.Pass,
            "fail" => TestsStatus.Fail,
            "not-run" => TestsStatus.NotRun,
            "deferred" => TestsStatus.Deferred,
            _ => TestsStatus.Unknown
        };

        return status != TestsStatus.Unknown;
    }

    private static bool TryParseBlockersStatus(string value, out BlockersStatus status)
    {
        var token = ReadLeadingWorkerResultToken(value);
        if (token.Length == 0)
        {
            status = BlockersStatus.Unknown;
            return false;
        }

        status = string.Equals(token, "none", StringComparison.OrdinalIgnoreCase)
            ? BlockersStatus.None
            : BlockersStatus.Present;
        return true;
    }

    private static bool IsNeedsWorkVerdict(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Equals("needs-work", StringComparison.Ordinal) ||
            normalized.StartsWith("needs-work ", StringComparison.Ordinal) ||
            normalized.StartsWith("needs-work-", StringComparison.Ordinal) ||
            normalized.StartsWith("needs-work:", StringComparison.Ordinal);
    }

    private static string ReadLeadingWorkerResultToken(string value)
    {
        var trimmed = value.Trim();
        var length = 0;
        while (length < trimmed.Length)
        {
            var ch = trimmed[length];
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                length++;
                continue;
            }

            break;
        }

        return length == 0 ? string.Empty : trimmed[..length].ToLowerInvariant();
    }

    private static bool IsNoChangeContractRole(TaskSpec task)
    {
        return task.RequiredRole switch
        {
            AgentRole.Researcher => true,
            AgentRole.Tester => !TaskRequestsFileChanges(task),
            _ => false
        };
    }

    private static bool TaskRequestsFileChanges(TaskSpec task)
    {
        var text = $"{task.Description}\n{task.VerificationPlan}".ToLowerInvariant();
        return Regex.IsMatch(
            text,
            @"\b(add|create|write|implement|update|modify|edit|fix)\b.{0,80}\b(test|tests|coverage|fixture|fixtures|source|file|files)\b|" +
            @"\b(test|tests|coverage|fixture|fixtures|source|file|files)\b.{0,80}\b(add|create|write|implement|update|modify|edit|fix)\b",
            RegexOptions.CultureInvariant);
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
        if (TryParseBlockersStatus(value, out var status))
        {
            return status == BlockersStatus.None;
        }

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

    private static IEnumerable<string> EnumerateEvidenceLines(TaskVerificationRecord verification)
    {
        foreach (var line in SplitRetainedLines(verification.StandardOutput))
        {
            yield return line;
        }

        foreach (var line in SplitRetainedLines(verification.StandardError))
        {
            yield return line;
        }

        foreach (var line in ReadArtifactLines(verification.StandardOutputPath))
        {
            yield return line;
        }

        foreach (var line in ReadArtifactLines(verification.StandardErrorPath))
        {
            yield return line;
        }
    }

    private static IEnumerable<string> SplitRetainedLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return line;
        }
    }

    private static IEnumerable<string> ReadArtifactLines(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            yield break;
        }

        StreamReader reader;
        try
        {
            reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (reader)
        {
            while (true)
            {
                string? line;
                try
                {
                    line = reader.ReadLine();
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (UnauthorizedAccessException)
                {
                    yield break;
                }

                if (line is null)
                {
                    yield break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line;
                }
            }
        }
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
