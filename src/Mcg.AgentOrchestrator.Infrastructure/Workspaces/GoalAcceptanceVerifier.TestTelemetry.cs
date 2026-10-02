using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using CommandResult = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.CommandResult;
using DotnetTestTelemetry = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.DotnetTestTelemetry;
using TrxCompletionEvidence = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.TrxCompletionEvidence;
using FocusedEvidenceSelectionCoverage = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceSelectionCoverage;
using FocusedEvidenceIdentityExtraction = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceIdentityExtraction;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalAcceptanceVerifierTestTelemetry
{
    internal static DotnetTestTelemetry? ResolveDotnetTestTelemetry(
        string[] arguments,
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptResultsPrefix,
        AcceptanceInvocationContext? invocationContext)
    {
        if (!GoalAcceptanceVerifier.IsDotnetTestCommand(arguments))
        {
            return null;
        }

        return ResolveTestTelemetry(check, environment, attemptResultsPrefix, invocationContext) with
        {
            Arguments = AddVstestTelemetryArguments(arguments, check, environment, attemptResultsPrefix, invocationContext)
        };
    }

    internal static DotnetTestTelemetry ResolveTestTelemetry(
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptResultsPrefix,
        AcceptanceInvocationContext? invocationContext,
        string? resultsDirectoryOverride = null) =>
        GoalAcceptanceVerifier.ResolveTestTelemetryCore(
            check,
            environment,
            attemptResultsPrefix,
            invocationContext?.Ordinal ?? 0,
            resultsDirectoryOverride);

    internal static void PrepareTestTelemetryForRun(DotnetTestTelemetry telemetry)
    {
        foreach (var path in telemetry.Paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Unable to clear stale TRX before the test run: {path}", ex);
            }

            if (File.Exists(path))
            {
                throw new IOException($"Unable to clear stale TRX before the test run: {path}");
            }
        }
    }

    internal static IReadOnlyList<string>? CopyCompletedTestReceiptsToAttemptFolder(
        IReadOnlyList<string>? sourcePaths,
        string? attemptPrefix)
    {
        if (sourcePaths is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(attemptPrefix))
        {
            return sourcePaths;
        }

        var receiptDirectory = Path.GetDirectoryName(attemptPrefix);
        if (string.IsNullOrWhiteSpace(receiptDirectory))
        {
            throw new InvalidOperationException(
                $"Acceptance attempt TRX prefix '{attemptPrefix}' does not identify an attempt receipt folder.");
        }

        var durablePaths = new string[sourcePaths.Count];
        for (var index = 0; index < sourcePaths.Count; index++)
        {
            var sourcePath = sourcePaths[index];
            if (!File.Exists(sourcePath))
            {
                durablePaths[index] = sourcePath;
                continue;
            }

            var destinationPath = Path.Combine(receiptDirectory, Path.GetFileName(sourcePath));
            try
            {
                Directory.CreateDirectory(receiptDirectory);
                if (!sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
                    File.Copy(sourcePath, temporaryPath, overwrite: true);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }

                durablePaths[index] = destinationPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Failed to preserve completed test receipt from '{sourcePath}' to attempt folder '{destinationPath}'.",
                    ex);
            }
        }

        return durablePaths;
    }

    internal static string[] AddVstestTelemetryArguments(
        string[] arguments,
        AcceptanceManifestCheck check,
        DotnetBuildEnvironment environment,
        string? attemptResultsPrefix,
        AcceptanceInvocationContext? invocationContext)
    {
        var telemetry = ResolveTestTelemetry(check, environment, attemptResultsPrefix, invocationContext);
        var directory = Path.GetDirectoryName(telemetry.Paths[0]) ?? Path.Combine(environment.ArtifactsPath, "TestResults");
        var fileName = Path.GetFileName(telemetry.Paths[0]);
        var effectiveArguments = new List<string>(arguments)
        {
            "--logger",
            $"trx;LogFileName={fileName}",
            "--results-directory",
            directory
        };

        return [.. effectiveArguments];
    }

    internal static void EmitMissingTrxReceiptIfNeeded(bool passed, DotnetTestTelemetry? telemetry)
    {
        if (!passed || telemetry is null)
        {
            return;
        }

        var missing = telemetry.Paths
            .Where(path => GoalAcceptanceVerifier.TryGetFileLength(path) <= 0)
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        Console.WriteLine($"TRX_TELEMETRY_UNAVAILABLE paths={GoalAcceptanceVerifier.QuoteProgressToken(string.Join(";", missing))}");
        Console.Out.Flush();
    }

    internal static IReadOnlyList<string> ExtractTrxFailureIdentities(IEnumerable<string>? trxPaths)
    {
        if (trxPaths is null)
        {
            return [];
        }

        var identities = new List<string>();
        foreach (var trxPath in trxPaths.Where(File.Exists))
        {
            try
            {
                identities.AddRange(AcceptanceTrxTestIdentityResolver.ExtractTrxFailureIdentities(trxPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // An unreadable receipt is evidence-inconclusive. Never infer a code failure from its output tail.
            }
        }

        return identities
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static TrxCompletionEvidence InspectTrxCompletionEvidence(IEnumerable<string>? trxPaths)
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

    internal static bool IsPassingTrxReceipt(
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

    internal static int? TryReadTrxCounter(XElement? counters, string name) =>
        int.TryParse(
            counters?.Attribute(name)?.Value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    internal static AcceptanceShardCompletionDecision DecideTestShardCompletion(
        CommandResult result,
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
        if (failedPredicate is null && GoalAcceptanceVerifier.IsInterrupted(result))
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
        if (failedPredicate is null && result.ExitCode != 0 && !allowNonzeroExit)
            failedPredicate = AcceptanceShardCompletionPredicates.NonzeroExit;

        var effectivePolicySignal = policyFailure ?? policySignal;
        if (failedPredicate is null && missingTrxCompatibility && effectivePolicySignal is null)
        {
            effectivePolicySignal = AcceptanceShardCompletionSignals.MissingTrxInjectedRunnerCompatibility;
        }

        return new AcceptanceShardCompletionDecision(
            failedPredicate is null,
            failedPredicate,
            GoalAcceptanceVerifier.IsInterrupted(result),
            result.ExitCode,
            trx.DiscoveredTestCount,
            trx.ExecutedTestCount,
            trx.Outcome,
            effectivePolicySignal,
            trx.NotExecutedTestCount);
    }

    internal static AcceptanceShardCompletionDecision DecideNonTestCommandCompletion(CommandResult result)
    {
        var failedPredicate = GoalAcceptanceVerifier.IsInterrupted(result)
            ? AcceptanceShardCompletionPredicates.TimedOut
            : result.ExitCode != 0
                ? AcceptanceShardCompletionPredicates.NonzeroExit
                : null;
        return new AcceptanceShardCompletionDecision(
            failedPredicate is null,
            failedPredicate,
            GoalAcceptanceVerifier.IsInterrupted(result),
            result.ExitCode,
            null,
            null,
            "not-applicable");
    }

    internal static FocusedEvidenceSelectionCoverage InspectFocusedEvidenceSelectionCoverage(
        IReadOnlyList<IReadOnlyList<FocusedEvidenceFilterToken>> selections,
        IEnumerable<string>? trxPaths)
    {
        if (selections.Count <= 1)
        {
            return FocusedEvidenceSelectionCoverage.Empty;
        }

        var extraction = ExtractTrxExecutedTestIdentities(trxPaths);
        var uncoveredSelections = selections
            .Where(selection => !selection.Any(token =>
                extraction.Identities.Any(identity => IsFocusedEvidenceIdentityMatch(identity, token.Value))))
            .Select(selection => string.Join("|", selection.Select(token => token.CanonicalToken)))
            .ToArray();
        return new FocusedEvidenceSelectionCoverage(uncoveredSelections, extraction.UnreadableReceiptPaths);
    }

    internal static FocusedEvidenceIdentityExtraction ExtractTrxExecutedTestIdentities(IEnumerable<string>? trxPaths)
    {
        if (trxPaths is null)
        {
            return new FocusedEvidenceIdentityExtraction([], []);
        }

        var identities = new List<string>();
        var unreadableReceiptPaths = new List<string>();
        foreach (var trxPath in trxPaths.Where(File.Exists))
        {
            try
            {
                var document = XDocument.Load(trxPath, LoadOptions.None);
                var definitionsByTestId = document
                    .Descendants()
                    .Where(element =>
                        element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
                    .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                identities.AddRange(document
                    .Descendants()
                    .Where(element => element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal))
                    .Select(result =>
                    {
                        definitionsByTestId.TryGetValue(
                            result.Attribute("testId")?.Value ?? string.Empty,
                            out var definition);
                        return AcceptanceTrxTestIdentityResolver.Resolve(result, definition) ??
                            result.Attribute("testId")?.Value?.Trim() ??
                            "unknown test";
                    }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                unreadableReceiptPaths.Add(trxPath);
            }
        }

        return new FocusedEvidenceIdentityExtraction(
            identities
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            unreadableReceiptPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static bool IsFocusedEvidenceIdentityMatch(string identity, string selection) =>
        identity.Contains(selection, StringComparison.OrdinalIgnoreCase);
}
