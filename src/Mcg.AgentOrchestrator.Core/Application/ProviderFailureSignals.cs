using System.Text.Json;
using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.Core.DispatchFailureClassifier;

namespace Mcg.AgentOrchestrator.Core;

public static class ProviderFailureSignals
{
    private enum ProviderTurnRecordKind
    {
        Other,
        Completed,
        Failed
    }

    // Codex emits these records under `codex exec --json`. The configured claude-cli and grok-cli
    // profiles emit plain text, so their failure vocabularies remain named gaps rather than guesses.
    private static readonly IReadOnlyDictionary<string, ProviderTurnRecordKind> ProviderTurnRecordVocabulary =
        new Dictionary<string, ProviderTurnRecordKind>(StringComparer.Ordinal)
        {
            ["turn.completed"] = ProviderTurnRecordKind.Completed,
            ["turn.failed"] = ProviderTurnRecordKind.Failed,
            ["error"] = ProviderTurnRecordKind.Failed
        };

    private static readonly Regex CodexCliDiagnosticPrefix = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?\s+ERROR\s+codex(?:[_:\w.-]*)?\b",
        RegexOptions.CultureInvariant);

    public static bool HasProviderNeutralProgressStallFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            task.LastVerification is { Succeeded: false } latest &&
            IsProviderNeutralProgressStallFailure(latest);
    }

    public static bool IsProviderNeutralProgressStallFailure(TaskVerificationRecord verification)
    {
        var output = string.Join(
            Environment.NewLine,
            verification.StandardOutput,
            verification.StandardError);

        return output.Contains("no observable progress", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("stall timeout", StringComparison.OrdinalIgnoreCase) &&
            output.Contains("heartbeat", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasRecoverableProviderConnectivityFailure(TaskSpec task)
    {
        if (!IsSubscriptionProviderCliDispatch(task))
        {
            return false;
        }

        if (task.Status == WorkTaskStatus.Failed &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderConnectivityFailure(latest))
        {
            return true;
        }

        return task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Pending &&
            task.LastVerification is null &&
            task.VerificationHistory.LastOrDefault() is { Succeeded: false } historyLatest &&
            IsRecoverableProviderConnectivityFailure(historyLatest);
    }

    public static bool HasRecoverableProviderAuthenticationFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            IsSubscriptionProviderCliDispatch(task) &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderAuthenticationFailure(latest);
    }

    public static bool HasRecoverableProviderModelRejectionFailure(TaskSpec task)
    {
        return task.Status == WorkTaskStatus.Failed &&
            IsSubscriptionProviderCliDispatch(task) &&
            task.LastVerification is { Succeeded: false } latest &&
            IsRecoverableProviderModelRejectionFailure(latest, task.RequiredRole);
    }

    public static int CountRecoverableProviderConnectivityFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderConnectivityFailure(verification));
    }

    public static int CountConsecutiveProviderInterruptionFailures(TaskSpec task)
    {
        var count = 0;
        for (var index = task.VerificationHistory.Count - 1; index >= 0; index--)
        {
            if (!IsProviderInterruptionFailure(task.VerificationHistory[index]) &&
                !ConductorGenerationInterruptionClassification.IsMarked(task.VerificationHistory[index]) &&
                task.VerificationHistory[index].CompletionVerdictRule != TaskOutcomeRules.VerificationTerminalContractIncomplete.Token)
            {
                break;
            }

            count++;
        }

        return count;
    }

    public static bool IsProviderInterruptionFailure(TaskVerificationRecord verification)
    {
        if (verification.EvidenceSource == VerificationEvidenceSource.Dispatch && verification.DispatchAttemptAt is not null &&
            HasExplicitProviderCancellation(verification.AuthoritativeStandardError ?? verification.StandardError))
            return true;
        var hasCompletedTurn = false;
        var hasFailedTurn = false;
        var lastProviderTurn = ProviderTurnRecordKind.Other;
        var inWorkerResultBlock = false;

        foreach (var rawLine in DispatchEvidenceSummaries.EnumerateEvidenceLines(
                     verification,
                     includeStandardOutput: true,
                     includeStandardError: false))
        {
            var line = rawLine.Trim();
            if (IsWorkerResultOpener(line))
            {
                inWorkerResultBlock = true;
                continue;
            }

            if (IsWorkerResultEndMarker(line))
            {
                inWorkerResultBlock = false;
                continue;
            }

            if (inWorkerResultBlock || !line.StartsWith('{'))
            {
                continue;
            }

            var recordKind = ClassifyProviderTurnRecord(line);
            hasCompletedTurn |= recordKind == ProviderTurnRecordKind.Completed;
            hasFailedTurn |= recordKind == ProviderTurnRecordKind.Failed;
            if (recordKind != ProviderTurnRecordKind.Other)
                lastProviderTurn = recordKind;
        }

        if (verification.EvidenceSource == VerificationEvidenceSource.Dispatch && verification.DispatchAttemptAt is not null)
        {
            // The streams are independently framed: an unfinished stdout result cannot
            // hide a terminal provider event in stderr.
            var inErrorResult = false;
            var inErrorFence = false;
            foreach (var line in DispatchEvidenceSummaries.SplitEvidenceLines(verification.AuthoritativeStandardError ?? string.Empty))
            {
                if (line.StartsWith("```", StringComparison.Ordinal)) { inErrorFence = !inErrorFence; continue; }
                if (IsWorkerResultOpener(line)) { inErrorResult = true; continue; }
                if (IsWorkerResultEndMarker(line)) { inErrorResult = false; continue; }
                if (inErrorFence || inErrorResult) continue;
                var recordKind = ClassifyProviderTurnRecord(line);
                if (recordKind != ProviderTurnRecordKind.Other)
                    lastProviderTurn = recordKind;
            }
        }

        foreach (var line in CodexRawAuditStream.ReadLines(verification.StandardOutputPath))
        {
            var recordKind = ClassifyProviderTurnRecord(line);
            hasCompletedTurn |= recordKind == ProviderTurnRecordKind.Completed;
            hasFailedTurn |= recordKind == ProviderTurnRecordKind.Failed;
            if (recordKind != ProviderTurnRecordKind.Other)
                lastProviderTurn = recordKind;
        }

        // A fast exit with low CPU and memory is a useful operator diagnostic, but recorded event
        // structure is the authority. Resource timing and provider message text are not inputs.
        return verification.EvidenceSource == VerificationEvidenceSource.Dispatch && verification.DispatchAttemptAt is not null
            ? lastProviderTurn == ProviderTurnRecordKind.Failed
            : !hasCompletedTurn && hasFailedTurn;
    }

    public static bool HasExplicitProviderCancellation(string standardError)
    {
        var inFence = false;
        var inResult = false;
        foreach (var line in standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (IsWorkerResultOpener(line)) { inResult = true; continue; }
            if (IsWorkerResultEndMarker(line)) { inResult = false; continue; }
            if (!inFence && !inResult && Regex.IsMatch(line,
                @"^(?:ERROR:|\[grok(?:-cli)?\])\s*(?:session ended[: ]+)?(?:reason=)?permission_cancelled\s*[.]?$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    private static ProviderTurnRecordKind ClassifyProviderTurnRecord(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ProviderTurnRecordKind.Other;
            }

            var root = document.RootElement;
            if ((!root.TryGetProperty("type", out var discriminator) &&
                 !root.TryGetProperty("kind", out discriminator)) ||
                discriminator.ValueKind != JsonValueKind.String)
            {
                return ProviderTurnRecordKind.Other;
            }

            var value = discriminator.GetString();
            return value is not null && ProviderTurnRecordVocabulary.TryGetValue(value, out var recordKind)
                ? recordKind
                : ProviderTurnRecordKind.Other;
        }
        catch (JsonException)
        {
            return ProviderTurnRecordKind.Other;
        }
    }

    public static bool IsRecoverableProviderAuthenticationFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        return TryGetProviderAuthenticationLine(verification, out _) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static bool IsRecoverableProviderConnectivityFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        return (verification.ProviderFailureKind == ProviderFailureKind.Connectivity ||
            TryGetProviderConnectivityLine(verification, out _)) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static bool IsRecoverableProviderModelRejectionFailure(TaskVerificationRecord verification) =>
        IsRecoverableProviderModelRejectionFailure(verification, requiredRole: null);

    internal static bool IsRecoverableProviderModelRejectionFailure(
        TaskVerificationRecord verification,
        AgentRole? requiredRole)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        return TryGetProviderModelRejectionLine(
                verification,
                ignoreAuthoritativePlannerContractLines: requiredRole == AgentRole.Planner,
                out _) &&
            !HasUsefulPreWorkOutput(verification.StandardOutput);
    }

    public static int CountRecoverableProviderModelRejectionFailures(TaskSpec task)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            IsRecoverableProviderModelRejectionFailure(verification, task.RequiredRole));
    }

    private static bool ContainsRecoverableProviderConnectivityText(string text)
    {
        return (text.Contains("websocket", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("os error", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("10013", StringComparison.OrdinalIgnoreCase))) ||
            text.Contains("Unable to connect to API", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ConnectionRefused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("DNS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("host resolution", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no such host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stream disconnected", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("transport", StringComparison.OrdinalIgnoreCase) &&
             (text.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("failed", StringComparison.OrdinalIgnoreCase))) ||
            IsApiErrorOverloadLine(text);
    }

    private static bool ContainsProviderAuthenticationText(string text)
    {
        return text.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Not signed in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("API Error 401", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("access token could not be refreshed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("refresh token was already used", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("refresh_token_reused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sign in again", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("log out and sign in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("authorization failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("invalid api key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProviderModelRejectionText(string text)
    {
        return (text.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("profile", StringComparison.OrdinalIgnoreCase)) &&
            (text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("invalid model", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("400", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasUsefulPreWorkOutput(string output)
    {
        return HasVerificationEvidence(output, string.Empty) ||
            output.Contains("HUMAN_INPUT:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Model fit:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Changed files:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Files changed:", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetProviderSignalLines(TaskVerificationRecord verification)
    {
        var inWorkerResultBlock = false;
        foreach (var rawLine in DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true))
        {
            var markerLine = rawLine.Trim();
            if (markerLine.Length == 0)
            {
                continue;
            }

            if (IsWorkerResultOpener(markerLine))
            {
                inWorkerResultBlock = true;
                continue;
            }

            if (IsWorkerResultEndMarker(markerLine))
            {
                inWorkerResultBlock = false;
                continue;
            }

            if (inWorkerResultBlock)
            {
                continue;
            }

            if (IsProviderErrorLine(rawLine) ||
                ProviderLimitEvidenceParser.IsProviderLimitEvidenceLine(rawLine))
            {
                yield return rawLine;
            }
        }

        foreach (var line in GetCliRefusalSignalLines(verification))
        {
            yield return line;
        }
    }

    internal static bool TryGetProviderAuthenticationLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetProviderSignalLines(verification))
        {
            if (ContainsProviderAuthenticationText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    internal static bool TryGetProviderConnectivityLine(TaskVerificationRecord verification, out string line)
    {
        foreach (var candidate in GetProviderSignalLines(verification))
        {
            if (ContainsRecoverableProviderConnectivityText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    internal static bool TryGetProviderModelRejectionLine(
        TaskVerificationRecord verification,
        out string line) =>
        TryGetProviderModelRejectionLine(
            verification,
            ignoreAuthoritativePlannerContractLines: true,
            out line);

    private static bool TryGetProviderModelRejectionLine(
        TaskVerificationRecord verification,
        bool ignoreAuthoritativePlannerContractLines,
        out string line)
    {
        foreach (var rawLine in DispatchEvidenceSummaries.EnumerateEvidenceLines(
            verification,
            includeStandardOutput: true,
            includeStandardError: true))
        {
            var candidate = rawLine.Trim();
            if ((!ignoreAuthoritativePlannerContractLines ||
                 !IsPlannerOutputContractFailureLine(candidate)) &&
                ContainsProviderModelRejectionText(candidate))
            {
                line = candidate;
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    private static bool IsPlannerOutputContractFailureLine(string line) =>
        line.StartsWith("Planner output contract failed:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Planner durable receipt failed revalidation:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Planner output contract could not persist the accepted plan:", StringComparison.OrdinalIgnoreCase);

    private static bool IsProviderErrorLine(string line)
    {
        return line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" : ERROR:", StringComparison.Ordinal) ||
            CodexCliDiagnosticPrefix.IsMatch(line);
    }
}
