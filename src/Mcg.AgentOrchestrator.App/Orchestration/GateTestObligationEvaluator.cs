using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateTestObligationEvaluator
{
    internal enum Outcome { Pending, Satisfied, SelectorNotFound, Failed, Waived }

    internal sealed record RenameScope(string RepositoryDirectory, string IntegrationBranch);

    internal sealed record Result(
        GateTestObligation Obligation, Outcome Outcome, IReadOnlyList<string> Selectors, string? SourceId,
        string? WaiverReason = null, IReadOnlyList<TestClassRenameEvidence.ClassRename>? RenameFollows = null,
        IReadOnlyList<AnsweredSelectorRebinding.Rebind>? AnswerRebinds = null)
    {
        internal string RenameProvenance => string.Join("; ", (RenameFollows ?? []).Select(rename =>
            $"GATE_TEST_OBLIGATION_RENAME_FOLLOWED id={Obligation.Id[..Math.Min(8, Obligation.Id.Length)]} " +
            $"old={rename.OldClass} new={rename.NewClass} old_path={rename.OldPath} new_path={rename.NewPath} " +
            $"similarity={rename.Similarity} evidence={rename.Evidence} source={SourceId}"));

        internal string AnswerRebindProvenance => string.Join("; ", (AnswerRebinds ?? []).Select(rebind =>
            $"GATE_TEST_OBLIGATION_ANSWER_REBOUND id={Obligation.Id[..Math.Min(8, Obligation.Id.Length)]} " +
            $"old={rebind.OldClass} new={rebind.NewClass} request={rebind.RequestId[..Math.Min(8, rebind.RequestId.Length)]} " +
            $"answer={rebind.AnswerId} source={SourceId}"));
    }

    internal static IReadOnlyList<Result> Evaluate(
        Goal goal, string orchestratorDirectory, string? candidateSha, RenameScope? renameScope = null,
        IEnumerable<HumanInputRequest>? answeredRequests = null)
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

        var renames = new Lazy<IReadOnlyList<TestClassRenameEvidence.ClassRename>>(() =>
            TestClassRenameEvidence.Read(renameScope!.RepositoryDirectory, renameScope.IntegrationBranch, candidateSha!));
        return obligations.Select(obligation =>
        {
            if (GateTestObligationWaivers.FindWaiver(goal, obligation) is { } waiver)
                return new Result(obligation, Outcome.Waived, obligation.Selectors, null, waiver.WaiverReason);
            var original = EvaluateObligation(obligation, sources, orchestratorDirectory);
            var result = original.Outcome == Outcome.SelectorNotFound && renameScope is not null &&
                !string.IsNullOrWhiteSpace(candidateSha)
                ? FollowRenames(original, sources, orchestratorDirectory, renames.Value) : original;
            return result.Outcome == Outcome.SelectorNotFound && answeredRequests is not null
                ? FollowAnswerRebinds(result, sources, orchestratorDirectory,
                    AnsweredSelectorRebinding.For(goal, obligation, answeredRequests)) : result;
        }).ToArray();
    }

    private static Result FollowRenames(Result original, IReadOnlyList<Source> sources,
        string orchestratorDirectory, IReadOnlyList<TestClassRenameEvidence.ClassRename> renames)
    {
        if (renames.Count == 0) return original;
        foreach (var source in sources)
        {
            if (original.Obligation.PlannerIdentity is { } identity && source.BriefVersion != identity.BriefVersion) continue;
            var evidence = GroupedGateReceiptTrxEvidence.Evaluate(original.Obligation.Selectors, source.Paths, orchestratorDirectory);
            var selectors = original.Obligation.Selectors.ToArray();
            var used = new List<TestClassRenameEvidence.ClassRename>();
            for (var index = 0; index < selectors.Length; index++)
            {
                var item = evidence[index];
                // Preserve found selectors, including failures and ambiguous matches.
                if (item.Passed + item.Failed + item.Superseded + item.Skipped > 0 || item.AmbiguousClassNames is not null) continue;
                var segments = selectors[index].Split('.');
                var matches = segments.SelectMany((segment, position) => renames
                    .Where(rename => rename.OldClass == segment).Select(rename => (position, rename))).ToArray();
                if (matches.Length != 1) continue;
                var (position, rename) = matches[0];
                segments[position] = rename.NewClass;
                selectors[index] = string.Join('.', segments);
                if (!used.Contains(rename)) used.Add(rename);
            }
            if (used.Count > 0 && GroupedGateReceiptTrxEvidence.Evaluate(selectors, source.Paths, orchestratorDirectory)
                .All(item => item.Satisfied))
                return new(original.Obligation, Outcome.Satisfied, original.Obligation.Selectors, source.Id, RenameFollows: used);
        }
        return original;
    }

    private static Result FollowAnswerRebinds(Result original, IReadOnlyList<Source> sources,
        string orchestratorDirectory, IReadOnlyList<AnsweredSelectorRebinding.Rebind> rebinds)
    {
        if (rebinds.Count == 0) return original;
        foreach (var source in sources)
        {
            if (original.Obligation.PlannerIdentity is { } identity && source.BriefVersion != identity.BriefVersion) continue;
            var evidence = GroupedGateReceiptTrxEvidence.Evaluate(original.Obligation.Selectors, source.Paths, orchestratorDirectory);
            var selectors = original.Obligation.Selectors.ToArray();
            var used = new List<AnsweredSelectorRebinding.Rebind>();
            for (var index = 0; index < selectors.Length; index++)
            {
                var item = evidence[index];
                // Preserve found selectors, including failures and ambiguous matches.
                if (item.Passed + item.Failed + item.Superseded + item.Skipped > 0 || item.AmbiguousClassNames is not null) continue;
                var segments = selectors[index].Split('.');
                var matches = segments.SelectMany((segment, position) => rebinds
                    .Where(rebind => rebind.OldClass == segment).Select(rebind => (position, rebind))).ToArray();
                if (matches.Length != 1) continue;
                var (position, rebind) = matches[0];
                segments[position] = rebind.NewClass;
                selectors[index] = string.Join('.', segments);
                if (!used.Contains(rebind)) used.Add(rebind);
            }
            if (used.Count > 0 && GroupedGateReceiptTrxEvidence.Evaluate(selectors, source.Paths, orchestratorDirectory)
                .All(item => item.Satisfied))
                return new(original.Obligation, Outcome.Satisfied, original.Obligation.Selectors, source.Id, AnswerRebinds: used);
        }
        return original;
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
