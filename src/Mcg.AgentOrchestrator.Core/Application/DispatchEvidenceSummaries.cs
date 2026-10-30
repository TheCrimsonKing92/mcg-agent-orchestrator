using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.Core.DispatchFailureClassifier;

namespace Mcg.AgentOrchestrator.Core;

internal static partial class DispatchEvidenceSummaries
{
    private const string DispatchHostMaxRuntimeReapMarker = "[dispatch-host] terminating worker tree: exceeded max runtime";

    internal static IEnumerable<string> EnumerateEvidenceLines(
        TaskVerificationRecord verification,
        bool includeStandardOutput,
        bool includeStandardError,
        bool includeRetainedText = true,
        bool includeArtifactText = true)
    {
        if (verification.EvidenceSource == VerificationEvidenceSource.Dispatch && verification.DispatchAttemptAt is not null)
        {
            if (includeStandardOutput)
                foreach (var line in SplitEvidenceLines(verification.AuthoritativeStandardOutput ?? string.Empty))
                    yield return line;
            if (includeStandardError)
                foreach (var line in SplitEvidenceLines(verification.AuthoritativeStandardError ?? string.Empty))
                    yield return line;
            yield break;
        }
        if (includeStandardOutput)
        {
            if (includeRetainedText)
            {
                foreach (var line in SplitEvidenceLines(verification.StandardOutput))
                {
                    yield return line;
                }
            }

            if (includeArtifactText)
            {
                foreach (var line in ReadEvidenceFileLines(verification.StandardOutputPath))
                {
                    yield return line;
                }
            }
        }

        if (includeStandardError)
        {
            if (includeRetainedText)
            {
                foreach (var line in SplitEvidenceLines(verification.StandardError))
                {
                    yield return line;
                }
            }

            if (includeArtifactText)
            {
                foreach (var line in ReadEvidenceFileLines(verification.StandardErrorPath))
                {
                    yield return line;
                }
            }
        }
    }

    private static IEnumerable<string> ReadEvidenceFileLines(string? path)
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
                    yield return line.TrimEnd();
                }
            }
        }
    }

    internal static string BuildEvidenceSummary(TaskVerificationRecord verification)
    {
        if (IsDirtyDispatchGuardFailure(verification))
        {
            return "dirty-dispatch-recovery";
        }

        if (TryGetPreflightFailureEvidenceLine(verification, out var preflightLine))
        {
            return BuildPreflightFailureEvidenceSummary(preflightLine);
        }

        if (ProviderFailureSignals.TryGetProviderAuthenticationLine(verification, out var providerAuthLine))
        {
            return TruncateEvidence(providerAuthLine);
        }

        if (ProviderFailureSignals.TryGetProviderConnectivityLine(verification, out var providerConnectivityLine))
        {
            return TruncateEvidence(providerConnectivityLine);
        }

        if (TryGetRecoverableSubscriptionLimitLine(verification, out var providerLimitLine))
        {
            return TruncateEvidence(providerLimitLine);
        }

        if (!HasVerificationEvidence(verification))
        {
            return string.Empty;
        }

        foreach (var rawLine in EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true))
        {
            if (HasVerificationEvidence(rawLine, string.Empty))
            {
                return TruncateEvidence(rawLine);
            }
        }

        return "Verification evidence found in output.";
    }

    internal static string TruncateEvidence(string evidence) =>
        evidence.Length > 200 ? evidence[..200] : evidence;

    internal static string BuildRecoverableSubscriptionLimitEvidenceSummary(TaskVerificationRecord verification) =>
        TryGetRecoverableSubscriptionLimitLine(verification, out var line)
            ? TruncateEvidence(line)
            : BuildEvidenceSummary(verification);

    internal static string BuildUnknownFailureEvidenceSummary(TaskVerificationRecord verification)
    {
        var substantiveLines = GetSubstantiveStandardErrorLines(verification)
            .TakeLast(3)
            .ToArray();

        return substantiveLines.Length == 0
            ? BuildEvidenceSummary(verification)
            : $"unknown-failure stderr-tail: {TruncateEvidence(string.Join(" | ", substantiveLines))}";
    }

    internal static string BuildRealFailureEvidenceSummary(TaskVerificationRecord verification)
    {
        var substantiveLines = GetSubstantiveStandardErrorLines(verification)
            .TakeLast(3)
            .ToArray();

        return substantiveLines.Length == 0
            ? BuildEvidenceSummary(verification)
            : $"real-failure stderr-tail: {TruncateEvidence(string.Join(" | ", substantiveLines))}";
    }

    internal static bool TryGetDispatchHostMaxRuntimeReapSummary(TaskVerificationRecord verification, out string summary)
    {
        foreach (var line in GetStandardErrorEvidenceLines(verification))
        {
            var markerIndex = line.IndexOf(DispatchHostMaxRuntimeReapMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var ceiling = DispatchHostMaxRuntimeMinutes().Match(line[(markerIndex + DispatchHostMaxRuntimeReapMarker.Length)..]);
            var ceilingText = ceiling.Success ? $" of {ceiling.Groups["minutes"].Value} min" : string.Empty;
            summary = $"Worker tree reaped by the dispatch host at its max runtime{ceilingText}; uncommitted tree contents are not evidence of a code failure.";
            return true;
        }

        summary = string.Empty;
        return false;
    }

    [GeneratedRegex(@"^ (?<minutes>[0-9]+) min\b", RegexOptions.CultureInvariant)]
    private static partial Regex DispatchHostMaxRuntimeMinutes();

    internal static bool HasScriptingFailureEvidence(TaskVerificationRecord verification) =>
        GetSubstantiveStandardErrorLines(verification).Any(IsScriptingFailureLine);

    private static bool IsScriptingFailureLine(string line) =>
        line.Contains("ParserError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("NativeCommandError", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("is not recognized as the name of a cmdlet", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("The string is missing the terminator", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetStandardErrorEvidenceLines(TaskVerificationRecord verification)
    {
        foreach (var line in EnumerateEvidenceLines(verification, includeStandardOutput: false, includeStandardError: true))
        {
            yield return line;
        }
    }

    internal static IEnumerable<string> GetSubstantiveStandardErrorLines(TaskVerificationRecord verification) =>
        GetStandardErrorEvidenceLines(verification).Where(line => !IsRunnerBookkeepingLine(line));

    internal static string BuildVerifiedNoNewCommitEvidenceSummary(TaskSpec task, TaskVerificationRecord verification)
    {
        var commitEvidence = TryGetKnownWorkerResultCommitSha(task, verification, out var commitSha)
            ? $"commit {commitSha}"
            : "known commit";
        var testsEvidence = WorkerResultBlockers.TryFindTests(verification, out var tests)
            ? $"; tests: {TruncateEvidence(tests)}"
            : string.Empty;

        return $"verified-no-new-commit: {commitEvidence}{testsEvidence}";
    }

    internal static string BuildVerifiedNoChangeRoundEvidenceSummary(TaskSpec task, TaskVerificationRecord verification)
    {
        var testsEvidence = WorkerResultBlockers.TryFindTests(verification, out var tests)
            ? $"; tests: {TruncateEvidence(tests)}"
            : string.Empty;

        var integration = task.LastDispatch?.PreDispatchIntegrationReceipt;
        if (integration?.Matches(task.LastDispatch?.GoalId, task, task.LastDispatch?.BaseCommit) == true)
        {
            return $"pre-dispatch integration satisfied retry: original {integration.OriginalCandidateSha}; " +
                $"main {integration.IntegratedMainSha}; candidate {integration.ResultingCandidateSha}{testsEvidence}";
        }

        return $"verified-no-change-round: candidate {task.LastDispatch?.BaseCommit ?? "unknown"}{testsEvidence}";
    }

    internal static string BuildProviderConnectivityEvidenceSummary(TaskVerificationRecord verification) =>
        ProviderFailureSignals.TryGetProviderConnectivityLine(verification, out var line)
            ? TruncateEvidence(line)
            : BuildEvidenceSummary(verification);

    internal static string BuildProviderBudgetExhaustionEvidenceSummary(
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        var dispatch = FindDispatchForVerification(task, verification) ?? task.LastDispatch;
        var providerName = dispatch?.ProviderName ?? dispatch?.WorkerName ?? "unknown";
        var binding = BuildProviderBindingKey(providerName, dispatch?.ClaudeCredentialSourceDirectory);
        var evidence = ProviderLimitSignals.TryGetProviderBudgetExhaustionLine(verification, out var line, out var source)
            ? TruncateEvidence(line)
            : "typed provider budget-exhaustion disposition";
        return $"provider-budget-exhausted source={source} provider={providerName} " +
            $"binding={binding.Key} binding-scope={binding.Scope} receipt={verification.CompletedAt:O}; evidence={evidence}";
    }

    internal static string BuildProviderModelRejectionEvidenceSummary(TaskVerificationRecord verification) =>
        ProviderFailureSignals.TryGetProviderModelRejectionLine(verification, out var line)
            ? TruncateEvidence(line)
            : BuildEvidenceSummary(verification);

    public static bool IsPreflightFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded ||
            !string.IsNullOrWhiteSpace(verification.StandardOutput) ||
            HasStandardOutputFileBytes(verification))
        {
            return false;
        }

        return TryGetPreflightFailureEvidenceLine(verification, out _);
    }

    internal static string BuildPreflightFailureEvidenceSummary(TaskVerificationRecord verification) =>
        TryGetPreflightFailureEvidenceLine(verification, out var line)
            ? BuildPreflightFailureEvidenceSummary(line)
            : "sandbox-preflight-failure";

    private static string BuildPreflightFailureEvidenceSummary(string evidence) =>
        $"sandbox-preflight-failure: {TruncateEvidence(evidence)}";

    private static bool TryGetPreflightFailureEvidenceLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetPreflightEvidenceLines(verification))
        {
            if (ContainsPreflightFailureText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetPreflightEvidenceLines(TaskVerificationRecord verification)
    {
        foreach (var line in EnumerateEvidenceLines(verification, includeStandardOutput: false, includeStandardError: true))
        {
            yield return line;
        }
    }

    internal static IEnumerable<string> SplitEvidenceLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line.TrimEnd();
            }
        }
    }

    private static bool ContainsPreflightFailureText(string text)
    {
        var hasPreflightContext =
            text.Contains("Low Integrity", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("integrity label", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("icacls", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("CreateProcessAsUser", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("preflight", StringComparison.OrdinalIgnoreCase);

        var hasFailureEvidence =
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failure", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("1312", StringComparison.OrdinalIgnoreCase);

        return hasPreflightContext && hasFailureEvidence;
    }
}
