using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GroupedReceiptTestResultPaths
{
    internal sealed record Substitution(string Lane, IReadOnlyList<string> RerunPaths, bool RerunPassed);
    internal sealed record Resolution(IReadOnlyList<string> Paths, IReadOnlyList<Substitution> Notes);

    internal static Resolution Resolve(
        AcceptanceVerificationResult verification, IReadOnlyList<string> normalizedPaths)
    {
        var paths = normalizedPaths.ToList();
        var notes = new List<Substitution>();
        foreach (var check in verification.Checks ?? [])
        {
            if (check.WithinAttemptRerun is not
                { FailedPredicate: AcceptanceShardCompletionPredicates.TimedOut, RerunTestResultPaths.Count: > 0 } evidence ||
                !evidence.RerunTestResultPaths.All(File.Exists))
                continue;

            var missingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in check.TestResultPaths ?? [])
            {
                if (string.IsNullOrWhiteSpace(path) || File.Exists(path)) continue;
                try { missingPaths.Add(Path.GetFullPath(path)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            var removed = paths.RemoveAll(path => missingPaths.Contains(path));
            paths.AddRange(evidence.RerunTestResultPaths);
            if (removed > 0) notes.Add(new Substitution(check.Name, evidence.RerunTestResultPaths, evidence.RerunPassed));
        }
        return new Resolution(AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(paths), notes);
    }

    internal static IReadOnlyList<string> Apply(
        TextWriter output, string kind, string receiptId,
        AcceptanceVerificationResult verification, IReadOnlyList<string> normalizedPaths)
    {
        var resolved = Resolve(verification, normalizedPaths);
        foreach (var note in resolved.Notes)
            output.WriteLine($"GROUPED_RECEIPT_RERUN_TRX kind={kind} receipt={receiptId} lane=\"{note.Lane}\" " +
                $"first_predicate=timed-out first_trx=none rerun_trx={string.Join(",", note.RerunPaths.Select(Path.GetFileName))} " +
                $"rerun_passed={note.RerunPassed.ToString().ToLowerInvariant()}");
        return resolved.Paths;
    }
}
