using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only grouped train receipts may recover RED ownership from fatal, incoherent TRX rows.
internal static class MergeTrainTimeoutAttribution
{
    internal static MergeTrainGateOutcome ResolveGateOutcome(
        AcceptanceCohortGateClassification classification,
        IReadOnlyList<string> resultPaths,
        string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members) => classification.Outcome switch
        {
            AcceptanceCohortGateOutcome.Passed => MergeTrainGateOutcome.Passed,
            AcceptanceCohortGateOutcome.Failed => MergeTrainGateOutcome.Failed,
            AcceptanceCohortGateOutcome.InfrastructureFailure when
                classification.InfrastructureReasonCode == AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent &&
                TryAttribute(resultPaths, workspacePath, members) is not null => MergeTrainGateOutcome.Failed,
            _ => MergeTrainGateOutcome.InfrastructureFailure
        };

    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths,
        string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members)
    {
        if (resultPaths.Count == 0) return null;
        var results = resultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        if (results.Any(result => result.Status != AcceptanceTrxReadStatus.Readable)) return null;
        if (resultPaths.Any(path => !HasOnlyFatalRowIncoherence(path))) return null;

        var hasFatalRow = false;
        foreach (var failure in results.SelectMany(result => result.Failures))
        {
            if (failure.Outcome.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase)) continue;
            // NotRunnable is fatal to the runner, but is not an eligible test RED here.
            if (!AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) ||
                failure.Outcome.Equals("NotRunnable", StringComparison.OrdinalIgnoreCase) ||
                ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null)
                return null;
            hasFatalRow = true;
        }

        return hasFatalRow
            ? MergeTrainRedAttribution.TryAttribute(resultPaths, workspacePath, members, out _)
            : null;
    }

    private static bool HasOnlyFatalRowIncoherence(string path)
    {
        // Readable XML alone does not prove a TRX is complete. Check its execution tallies
        // including the fatal outcomes before assigning a malformed document to a member.
        try
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var root = XDocument.Load(path).Root;
            if (root?.Name != ns + "TestRun") return false;
            var rows = root.Element(ns + "Results")?.Elements(ns + "UnitTestResult").ToArray() ?? [];
            var counters = root.Element(ns + "ResultSummary")?.Element(ns + "Counters");
            if (rows.Length == 0 || counters is null) return false;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var outcome = (string?)row.Attribute("outcome") ?? string.Empty;
                if (outcome.ToUpperInvariant() is not ("PASSED" or "FAILED" or "NOTEXECUTED" or "TIMEOUT" or "ERROR" or "ABORTED"))
                    return false;
                counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
            }
            var executed = rows.Length - counts.GetValueOrDefault("NotExecuted");
            return executed > 0 && Matches("total", rows.Length) && Matches("executed", executed) &&
                Matches("passed", counts.GetValueOrDefault("Passed")) && Matches("failed", counts.GetValueOrDefault("Failed")) &&
                MatchesOptional("timeout", counts.GetValueOrDefault("Timeout")) &&
                MatchesOptional("error", counts.GetValueOrDefault("Error")) &&
                MatchesOptional("aborted", counts.GetValueOrDefault("Aborted"));

            bool Matches(string name, int expected) => int.TryParse(
                counters.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out var actual) && actual == expected;
            bool MatchesOptional(string name, int expected) =>
                !counters.Attributes().Any(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)) || Matches(name, expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }
}
