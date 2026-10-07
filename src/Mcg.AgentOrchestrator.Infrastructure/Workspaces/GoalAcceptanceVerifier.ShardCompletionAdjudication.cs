using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record TrxCompletionEvidence(
    int? DiscoveredTestCount,
    int? ExecutedTestCount,
    int? NotExecutedTestCount,
    string Outcome,
    bool Passed,
    string? FailedPredicate,
    bool AssemblyCleanupOnly = false);

internal static class AcceptanceShardCompletionAdjudicator
{
    internal static TrxCompletionEvidence InspectTrxCompletion(IEnumerable<string>? trxPaths)
    {
        var paths = trxPaths?.ToArray() ?? [];
        if (paths.Length == 0 || paths.All(path => !File.Exists(path)))
        {
            return new TrxCompletionEvidence(null, null, null, "missing", false, AcceptanceShardCompletionPredicates.MissingTrx);
        }

        var discovered = 0;
        var executed = 0;
        var notExecuted = 0;
        var sawReceipt = false;
        var allPassed = true;
        AcceptanceAssemblyCleanupEvidence.RowCounts cleanupCounts = default;
        var outcomes = new List<string>();
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                var document = XDocument.Load(path, LoadOptions.None);
                var counters = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("Counters", StringComparison.Ordinal));
                var results = document.Descendants().Where(element =>
                    element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal)).ToArray();
                var definitions = document.Descendants().Count(element =>
                    element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal));
                var receiptDiscovered = TryReadTrxCounter(counters, "total") ?? definitions;
                var receiptExecuted = TryReadTrxCounter(counters, "executed") ?? results.Length;
                var receiptNotExecuted = TryReadTrxCounter(counters, "notExecuted") ?? 0;
                var resultSummary = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("ResultSummary", StringComparison.Ordinal));
                var summaryOutcome = resultSummary?.Attribute("outcome")?.Value;
                var receiptPassed = IsPassingTrxReceipt(summaryOutcome, counters, results);
                cleanupCounts = cleanupCounts.Add(AcceptanceAssemblyCleanupEvidence.Classify(results));

                sawReceipt = true;
                discovered = checked(discovered + receiptDiscovered);
                executed = checked(executed + receiptExecuted);
                notExecuted = checked(notExecuted + receiptNotExecuted);
                allPassed &= receiptPassed;
                outcomes.Add(string.IsNullOrWhiteSpace(summaryOutcome) ? "results-only" : summaryOutcome);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or OverflowException)
            {
                return new TrxCompletionEvidence(null, null, null, "malformed", false, AcceptanceShardCompletionPredicates.MalformedTrx);
            }
        }

        if (!sawReceipt)
        {
            return new TrxCompletionEvidence(null, null, null, "missing", false, AcceptanceShardCompletionPredicates.MissingTrx);
        }

        return new TrxCompletionEvidence(
            discovered,
            executed,
            notExecuted,
            string.Join('+', outcomes.Distinct(StringComparer.OrdinalIgnoreCase)),
            allPassed,
            null,
            outcomes.Count == paths.Length && cleanupCounts.IsCleanupOnly);
    }

    private static bool IsPassingTrxReceipt(
        string? summaryOutcome,
        XElement? counters,
        IReadOnlyList<XElement> results)
    {
        var summaryCanBeGreen = string.IsNullOrWhiteSpace(summaryOutcome) ||
            summaryOutcome.Equals("Passed", StringComparison.OrdinalIgnoreCase) ||
            summaryOutcome.Equals("Completed", StringComparison.OrdinalIgnoreCase);
        if (!summaryCanBeGreen)
        {
            return false;
        }

        if (AcceptanceTrxOutcomeTaxonomy.HasFatalCounter(name => TryReadTrxCounter(counters, name)))
        {
            return false;
        }

        return results.All(result =>
        {
            var outcome = result.Attribute("outcome")?.Value;
            return string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome, "Skipped", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int? TryReadTrxCounter(XElement? counters, string name) =>
        int.TryParse(
            counters?.Attribute(name)?.Value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    internal static AcceptanceShardCompletionDecision DecideTestShard(
        bool interrupted,
        int exitCode,
        TrxCompletionEvidence trx,
        string? policyFailure = null,
        string? policySignal = null,
        bool allowNonzeroExit = false,
        bool requireTrxEvidence = true)
    {
        var missingTrxCompatibility = !requireTrxEvidence &&
            string.Equals(
                trx.FailedPredicate,
                AcceptanceShardCompletionPredicates.MissingTrx,
                StringComparison.Ordinal);
        var failedPredicate = policyFailure;
        if (failedPredicate is null && interrupted)
            failedPredicate = AcceptanceShardCompletionPredicates.TimedOut;
        if (failedPredicate is null && !missingTrxCompatibility && trx.FailedPredicate is not null)
            failedPredicate = trx.FailedPredicate;
        if (failedPredicate is null && !missingTrxCompatibility && trx.DiscoveredTestCount == 0)
            failedPredicate = AcceptanceShardCompletionPredicates.ZeroTests;
        if (failedPredicate is null && !missingTrxCompatibility &&
            trx.DiscoveredTestCount != (trx.ExecutedTestCount ?? 0) + (trx.NotExecutedTestCount ?? 0))
            failedPredicate = AcceptanceShardCompletionPredicates.IncompleteExecution;
        if (failedPredicate is null && !missingTrxCompatibility && !trx.Passed)
            failedPredicate = trx.AssemblyCleanupOnly
                ? AcceptanceShardCompletionPredicates.AssemblyCleanupFailure
                : AcceptanceShardCompletionPredicates.FailingTrx;
        if (failedPredicate is null && exitCode != 0 && !allowNonzeroExit)
            failedPredicate = AcceptanceShardCompletionPredicates.NonzeroExit;

        var effectivePolicySignal = policyFailure ?? policySignal;
        if (failedPredicate is null && missingTrxCompatibility && effectivePolicySignal is null)
        {
            effectivePolicySignal = AcceptanceShardCompletionSignals.MissingTrxInjectedRunnerCompatibility;
        }

        return new AcceptanceShardCompletionDecision(
            failedPredicate is null,
            failedPredicate,
            interrupted,
            exitCode,
            trx.DiscoveredTestCount,
            trx.ExecutedTestCount,
            trx.Outcome,
            effectivePolicySignal,
            trx.NotExecutedTestCount);
    }

    internal static AcceptanceShardCompletionDecision DecideNonTestCommand(bool interrupted, int exitCode)
    {
        var failedPredicate = interrupted
            ? AcceptanceShardCompletionPredicates.TimedOut
            : exitCode != 0
                ? AcceptanceShardCompletionPredicates.NonzeroExit
                : null;
        return new AcceptanceShardCompletionDecision(
            failedPredicate is null,
            failedPredicate,
            interrupted,
            exitCode,
            null,
            null,
            "not-applicable");
    }

    internal static AcceptanceShardCompletionDecision InferPartition(AcceptanceCheckResult result, bool captureLimitFailureName)
    {
        if (result.Passed)
        {
            return new AcceptanceShardCompletionDecision(
                true,
                null,
                false,
                result.ExitCode,
                result.DiscoveredTestCount,
                result.ExecutedTestCount,
                "not-applicable",
                result.FailureClassification);
        }

        var predicate = result.FailureClassification ??
            (result.Name.StartsWith("acceptance-check-timeout:", StringComparison.Ordinal) ||
             captureLimitFailureName
                ? AcceptanceShardCompletionPredicates.TimedOut
                : result.ExitCode != 0
                    ? AcceptanceShardCompletionPredicates.NonzeroExit
                    : AcceptanceShardCompletionPredicates.CheckFailed);
        return new AcceptanceShardCompletionDecision(
            false,
            predicate,
            predicate.Equals(AcceptanceShardCompletionPredicates.TimedOut, StringComparison.Ordinal),
            result.ExitCode,
            result.DiscoveredTestCount,
            result.ExecutedTestCount,
            "not-available",
            result.FailureClassification);
    }

}
