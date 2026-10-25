using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateTestObligationEvaluator
{
    internal enum Outcome { Pending, Satisfied, SelectorNotFound, Failed, Waived }

    internal sealed record Result(
        GateTestObligation Obligation, Outcome Outcome, IReadOnlyList<string> Selectors, string? SourceId,
        string? WaiverReason = null);

    internal static IReadOnlyList<Result> Evaluate(Goal goal, string orchestratorDirectory, string? candidateSha)
    {
        var obligations = goal.GateTestObligations
            .Where(obligation => obligation.BriefVersion == goal.AuthoritativeBrief.Version).ToArray();
        if (obligations.Length == 0) return [];

        var sources = ProspectiveEvidenceGateResolver.ReadAttempts(orchestratorDirectory, goal.Id, candidateSha)
            .Select(attempt => new Source(attempt.Id, attempt.CompletedAt, attempt.Paths, attempt.BriefVersion))
            .Concat(GroupedGateReceiptTrxEvidence.ReadPassedReceipts(orchestratorDirectory, goal.Id, candidateSha)
                .Select(receipt => new Source(receipt.ReceiptId, receipt.CompletedAt, receipt.TrxPaths, receipt.BriefVersion)))
            .Where(source => AllPathsExist(source.Paths, orchestratorDirectory))
            .OrderByDescending(source => source.CompletedAt)
            .ThenBy(source => source.Id, StringComparer.Ordinal).ToArray();

        return obligations.Select(obligation => GateTestObligationWaivers.FindWaiver(goal, obligation) is { } waiver
            ? new Result(obligation, Outcome.Waived, obligation.Selectors, null, waiver.WaiverReason)
            : EvaluateObligation(obligation, sources, orchestratorDirectory)).ToArray();
    }

    private static Result EvaluateObligation(
        GateTestObligation obligation, IReadOnlyList<Source> sources, string orchestratorDirectory)
    {
        Result? newest = null;
        foreach (var source in sources)
        {
            if (obligation.PlannerIdentity is not null && source.BriefVersion != obligation.PlannerIdentity.BriefVersion) continue;
            var evidence = GroupedGateReceiptTrxEvidence.Evaluate(
                obligation.Selectors, source.Paths, orchestratorDirectory);
            if (evidence.All(item => item.Satisfied))
                return new(obligation, Outcome.Satisfied, obligation.Selectors, source.Id);
            if (newest is not null) continue;

            var failed = evidence.Where(item => !item.Satisfied && item.Failed > 0)
                .Select(item => item.Selector).ToArray();
            newest = failed.Length > 0
                ? new(obligation, Outcome.Failed, failed, source.Id)
                : new(obligation, Outcome.SelectorNotFound,
                    evidence.Where(item => !item.Satisfied && item.Failed == 0 &&
                            (item.AmbiguousClassNames is not null || item.Passed + item.Failed + item.Superseded == 0))
                        .Select(item => item.AmbiguousClassNames is not null ? item.ReportedEntry : item.Selector)
                        .ToArray(), source.Id);
        }
        return newest ?? new(obligation, Outcome.Pending, obligation.Selectors, null);
    }

    private static bool AllPathsExist(IReadOnlyList<string> paths, string orchestratorDirectory)
    {
        try { return paths.All(path => File.Exists(Path.GetFullPath(path, orchestratorDirectory))); }
        catch (Exception) { return false; }
    }

    private sealed record Source(string Id, DateTimeOffset CompletedAt, IReadOnlyList<string> Paths, int? BriefVersion);
}
