using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Narrow ambiguous Go evidence within one source; never pool rows from different attempts.
internal sealed class GoTestPackageResolution(string repositoryDirectory, string integrationBranch, string candidateSha)
{
    internal sealed record Resolution(string Selector, string Package, string Directory);

    private readonly Lazy<IReadOnlySet<string>> changedDirectories = new(() =>
        GoTestPackageDiffEvidence.Read(repositoryDirectory, integrationBranch, candidateSha));

    internal GateTestObligationEvaluator.Result Resolve(GateTestObligationEvaluator.Result original,
        IReadOnlyList<GateTestObligationEvaluator.Source> sources, string orchestratorDirectory)
    {
        var fallback = original;
        foreach (var source in sources)
        {
            if (original.Obligation.PlannerIdentity is { } identity && source.BriefVersion != identity.BriefVersion) continue;
            ProspectiveEvidenceTrxRows.Row[] rows;
            try
            {
                rows = source.Paths.Select(path => Path.GetFullPath(path, orchestratorDirectory))
                    .SelectMany(ProspectiveEvidenceTrxRows.Read).ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // As in ordinary grading, an unreadable receipt cannot supply partial evidence.
                continue;
            }
            var evidence = GroupedGateReceiptTrxEvidence.Evaluate(
                original.Obligation.Selectors, source.Paths, orchestratorDirectory).ToArray();
            var used = new List<Resolution>();
            for (var index = 0; index < evidence.Length; index++)
            {
                var selector = evidence[index].Selector;
                if (evidence[index].AmbiguousClassNames is null ||
                    selector.IndexOfAny(['.', '/', '\\', '(', '[', '+']) >= 0) continue;
                var matches = ProspectiveEvidenceTrxRows.Match(selector, rows);
                // Only Go rows carry CodeBase. Missing or mixed identities cannot authorize narrowing.
                var packages = matches.Select(row => (Row: row,
                    Directory: GoTestPackageDiffEvidence.NormalizeDirectory(repositoryDirectory, row.CodeBase))).ToArray();
                if (packages.Any(package => package.Directory is null || package.Row.ClassName is null)) continue;
                var changed = packages.Where(package => changedDirectories.Value.Contains(package.Directory!))
                    .Select(package => package.Directory!).Distinct(StringComparer.Ordinal).ToArray();
                if (changed.Length != 1) continue;
                var selected = packages.Where(package => package.Directory == changed[0]).Select(package => package.Row).ToArray();
                var imports = selected.Select(row => row.ClassName!).Distinct(StringComparer.Ordinal).ToArray();
                if (imports.Length != 1) continue;
                evidence[index] = EvaluateSelected(selector, selected);
                used.Add(new(selector, imports[0], changed[0]));
            }
            if (used.Count == 0) continue;
            var result = GateTestObligationEvaluator.Grade(original.Obligation, source.Id, evidence)
                with { PackageResolutions = used };
            if (result.Outcome == GateTestObligationEvaluator.Outcome.Satisfied) return result;
            if (source.Id == original.SourceId) fallback = result;
        }
        return fallback;
    }

    private static GroupedGateReceiptTrxEvidence.SelectorEvidence EvaluateSelected(string selector,
        ProspectiveEvidenceTrxRows.Row[] matches)
    {
        // Preserve the gate's rerun and skipped-row rules after selecting by directory alone.
        var passed = matches.Count(row => row.Passed);
        var superseded = matches.Count(row => !row.Passed && matches.Any(rerun => rerun.Passed &&
            string.Equals(rerun.FullIdentity, row.FullIdentity, StringComparison.Ordinal) &&
            !string.Equals(rerun.Path, row.Path, StringComparison.OrdinalIgnoreCase)));
        var skipped = passed > 0 && matches.All(row => row.Passed || row.Outcome == "NotExecuted")
            ? matches.Length - passed - superseded : 0;
        var failed = matches.Length - passed - superseded - skipped;
        return new(selector, passed, failed, superseded, skipped, passed > 0 && failed == 0);
    }
}
