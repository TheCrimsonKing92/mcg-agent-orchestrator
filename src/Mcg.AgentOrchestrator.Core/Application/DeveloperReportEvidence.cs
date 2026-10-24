namespace Mcg.AgentOrchestrator.Core;

// Compares a criterion's explicit report requirement with the latest recorded Developer round.
// Candidate binding and wait completion remain the closure's responsibility.
internal static class DeveloperReportEvidence
{
    internal static bool TryMatch(string criterion, TaskSpec developer, out string? tests, out string? skipReason)
    {
        tests = null;
        skipReason = null;
        var requiredField = FindRequiredField(criterion);
        if (requiredField is null)
            return false;
        var required = DeveloperDeferredTestClassNames.Parse(requiredField);
        if (required.Count == 0)
            return false;

        var verification = developer.LastVerification;
        var dispatch = developer.LastDispatch;
        if (verification is null || dispatch is null ||
            verification.EvidenceSource == VerificationEvidenceSource.Manual || !verification.WorkerResultPresent ||
            verification.CompletedAt == default || dispatch.DispatchedAt == default ||
            verification.CompletedAt < dispatch.DispatchedAt ||
            !WorkerResultBlockers.TryFindTests(
                verification.AuthoritativeStandardOutput ?? verification.StandardOutput, out var reportedField) ||
            !FirstWordIsDeferred(reportedField))
        {
            skipReason = "developer-report-missing";
            return false;
        }

        if (!new HashSet<string>(required, StringComparer.Ordinal)
            .SetEquals(DeveloperDeferredTestClassNames.Parse(reportedField)))
        {
            skipReason = "developer-report-differs";
            return false;
        }

        tests = reportedField;
        return true;
    }

    private static string? FindRequiredField(string criterion)
    {
        for (var offset = 0; offset < criterion.Length;)
        {
            var start = criterion.IndexOf('`', offset);
            if (start < 0)
                break;
            var end = criterion.IndexOf('`', start + 1);
            if (end < 0)
                break;
            var span = criterion[(start + 1)..end].Trim();
            if (span.StartsWith("tests:", StringComparison.Ordinal) && FirstWordIsDeferred(span["tests:".Length..]))
                return span["tests:".Length..].Trim();
            offset = end + 1;
        }
        return null;
    }

    private static bool FirstWordIsDeferred(string field)
    {
        var value = field.TrimStart();
        const string word = "deferred";
        return value.StartsWith(word, StringComparison.Ordinal) &&
            (value.Length == word.Length || char.IsWhiteSpace(value[word.Length]) ||
             value[word.Length] is '-' or ':' or '\u2013' or '\u2014');
    }
}
