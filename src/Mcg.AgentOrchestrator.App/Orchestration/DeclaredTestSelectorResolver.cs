namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeclaredTestSelectorResolver
{
    internal static GroupedGateReceiptTrxEvidence.SelectorEvidence Evaluate(
        string selector, IReadOnlyList<DeclaredTestReportRows.Row> rows)
    {
        var matches = rows.Where(row => row.Identity == selector || row.Name == selector ||
            row.Identity.StartsWith(selector + "::", StringComparison.Ordinal) ||
            // Read compatibility for persisted obligations only; never emit this form.
            row.Suite.Count > 0 && string.Join("::", row.Suite) + "." + row.Name == selector).ToArray();
        var passed = matches.Count(row => row.Outcome == "Passed");
        var superseded = matches.Count(row => row.Outcome != "Passed" && rows.Any(rerun =>
            rerun.Outcome == "Passed" && rerun.Identity == row.Identity &&
            !string.Equals(rerun.Path, row.Path, StringComparison.OrdinalIgnoreCase)));
        var skipped = passed > 0 && matches.All(row => row.Outcome is "Passed" or "NotExecuted")
            ? matches.Length - passed - superseded : 0;
        var failed = matches.Length - passed - superseded - skipped;
        return new(selector, passed, failed, superseded, skipped, passed > 0 && failed == 0);
    }
}
