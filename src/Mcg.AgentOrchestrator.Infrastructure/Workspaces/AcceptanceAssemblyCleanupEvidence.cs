using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceAssemblyCleanupEvidence
{
    private const string CleanupRowPrefix = "[Test Assembly Cleanup Failure (";
    private const string StderrLinePrefix = "assembly-temp-cleanup";
    private const int MaxStderrLines = 20;
    private const string NoDiagnosticLines =
        "assembly-temp-cleanup: no cleanup diagnostic lines were captured on the test host error stream.";

    internal readonly record struct RowCounts(
        int Passed, int NonPassing, int CleanupFailures, bool HasCleanupRows)
    {
        internal bool IsCleanupOnly => Passed > 0 && NonPassing > 0 && NonPassing == CleanupFailures;

        internal RowCounts Add(RowCounts other) => new(
            checked(Passed + other.Passed), checked(NonPassing + other.NonPassing),
            checked(CleanupFailures + other.CleanupFailures), HasCleanupRows || other.HasCleanupRows);
    }

    internal static RowCounts Classify(IEnumerable<XElement> results)
    {
        var passed = 0;
        var nonPassing = 0;
        var cleanupFailures = 0;
        var hasCleanupRows = false;
        foreach (var result in results)
        {
            var isCleanup = (result.Attribute("testName")?.Value.Trim() ?? string.Empty)
                .StartsWith(CleanupRowPrefix, StringComparison.Ordinal);
            hasCleanupRows |= isCleanup;
            var outcome = result.Attribute("outcome")?.Value.Trim();
            if (string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase))
            {
                passed++;
            }
            else if (!string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(outcome, "Skipped", StringComparison.OrdinalIgnoreCase))
            {
                nonPassing++;
                if (isCleanup) cleanupFailures++;
            }
        }
        return new RowCounts(passed, nonPassing, cleanupFailures, hasCleanupRows);
    }

    internal static (bool HasCleanupRows, bool IsCleanupOnly) InspectReceipts(IEnumerable<string>? paths)
    {
        RowCounts counts = default;
        var unreadable = false;
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                unreadable = true;
                continue;
            }
            try
            {
                counts = counts.Add(Classify(XDocument.Load(path).Descendants().Where(element =>
                    element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Xml.XmlException or OverflowException)
            {
                unreadable = true;
            }
        }
        return (counts.HasCleanupRows, !unreadable && counts.IsCleanupOnly);
    }

    internal static IReadOnlyList<string> SelectStderrLines(string? stderr, string? stderrPath)
    {
        var lines = new List<string>();
        var matched = 0;
        try
        {
            using var reader = !string.IsNullOrEmpty(stderr)
                ? (TextReader)new StringReader(stderr)
                : !string.IsNullOrWhiteSpace(stderrPath) && File.Exists(stderrPath) ? File.OpenText(stderrPath) : null;
            while (reader?.ReadLine() is { } line)
            {
                if (!line.TrimStart().StartsWith(StderrLinePrefix, StringComparison.Ordinal)) continue;
                matched++;
                if (lines.Count < MaxStderrLines) lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Captured diagnostics are supplementary; unavailable capture must not mask the verdict.
        }
        if (matched == 0) lines.Add(NoDiagnosticLines);
        else if (matched > MaxStderrLines)
            lines.Add($"{matched - MaxStderrLines} more assembly-temp-cleanup lines omitted.");
        return lines;
    }

    internal static AcceptanceCheckResult AttachCleanupCause(AcceptanceCheckResult check)
    {
        var classification = string.IsNullOrWhiteSpace(check.FailureClassification)
            ? null : check.FailureClassification.Trim();
        // Rerun relabelling preserves the original decision and stderr on the first-run verdict.
        var relabelledCleanup = classification == AcceptanceFailureClassifications.SharedGateApparatusInvalidated &&
            check.CompletionDecision?.FailedPredicate == AcceptanceShardCompletionPredicates.AssemblyCleanupFailure;
        if (check.Passed || check.FailureCauseEvidence is not null ||
            classification is not null &&
            classification != AcceptanceFailureClassifications.AssemblyCleanupFailure && !relabelledCleanup)
        {
            return check;
        }

        var cleanupOnly = check.CompletionDecision is { } decision
            ? !decision.Passed && decision.FailedPredicate == AcceptanceShardCompletionPredicates.AssemblyCleanupFailure
            : InspectReceipts(check.TestResultPaths).IsCleanupOnly;
        if (!cleanupOnly) return check;

        classification ??= AcceptanceFailureClassifications.AssemblyCleanupFailure;
        var evidence = $"check={JsonSerializer.Serialize(check.Name)}; failureClassification={JsonSerializer.Serialize(classification)}";
        evidence += Environment.NewLine + string.Join(Environment.NewLine,
            SelectStderrLines(check.ProcessStderr, check.ProcessStderrPath));
        return check with
        {
            FailureClassification = classification,
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus, evidence, check.Name, classification)
        };
    }
}
