namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IStewardTriageEngine
{
    Task<StewardTriageBatch> TriageAsync(
        StewardBriefingBundle bundle,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class StewardComposer : IStewardTriageEngine
{
    public Task<StewardTriageBatch> TriageAsync(
        StewardBriefingBundle bundle,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var cards = bundle.Escalations
            .Where(item => item.Category == StewardEscalationCategory.Normal)
            .OrderByDescending(item => item.SeverityRank)
            .ThenBy(item => item.RaisedAt)
            .Select(item => ComposeDecisionCard(bundle, item, now))
            .ToList();

        return Task.FromResult(new StewardTriageBatch(
            cards.Select(output => output.Value).ToList(),
            cards.Select(output => output.Receipt).ToList()));
    }

    public StewardOutput<ControlPlaneDecisionCard> ComposeDecisionCard(
        StewardBriefingBundle bundle,
        StewardEscalationItem escalation,
        DateTimeOffset now)
    {
        var precedent = bundle.MatchedPrecedents.FirstOrDefault(item =>
            item.Kind.Equals(escalation.Kind, StringComparison.OrdinalIgnoreCase) &&
            item.CauseFingerprint.Equals(escalation.CauseFingerprint, StringComparison.OrdinalIgnoreCase));
        var body = RenderDecisionCardBody(bundle, precedent);
        var card = new ControlPlaneDecisionCard(
            ControlPlaneCardSource.StewardTriage,
            escalation.GoalId,
            escalation.Kind,
            escalation.Id,
            escalation.CauseFingerprint,
            escalation.Title,
            body,
            escalation.RaisedAt);
        var measurement = new StewardCardLoadMeasurement(
            LandedGoals: Math.Max(0, bundle.GoalTasks.Count(item => item.State.Equals("Landed", StringComparison.OrdinalIgnoreCase))),
            NovelCards: precedent is null ? 1 : 0,
            PrecedentCoveredCards: precedent is null ? 0 : 1);
        var receipt = Receipt(
            StewardOutputKind.DecisionCard,
            new { bundle, escalation },
            now,
            [escalation.Id],
            [StewardInboxDisposition.CardCreated(escalation.Id, card.DedupKey)],
            $"decision-card kind={escalation.Kind} precedentCovered={precedent is not null}",
            measurement);

        return new StewardOutput<ControlPlaneDecisionCard>(card, receipt);
    }

    public StewardOutput<StewardStormCollapseJudgment> ComposeStormCollapseJudgment(
        StewardBriefingBundle bundle,
        string kind,
        int stormThreshold,
        DateTimeOffset now)
    {
        var matching = bundle.Escalations
            .Where(item => item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RaisedAt)
            .ToList();
        var cards = matching
            .Select(item => ComposeDecisionCard(bundle, item, now).Value)
            .ToList();
        var fingerprint = matching.Count == 0
            ? ControlPlaneDecisionCard.ComputeFingerprint(kind)
            : ControlPlaneDecisionCard.ComputeFingerprint(string.Join('|', matching.Select(item => item.CauseFingerprint)));
        var judgment = new StewardStormCollapseJudgment(
            kind,
            fingerprint,
            matching.Count,
            matching.Count >= stormThreshold,
            cards);
        var dispositions = matching
            .Zip(cards, (item, card) => StewardInboxDisposition.CardCreated(item.Id, card.DedupKey))
            .ToList();
        var receipt = Receipt(
            StewardOutputKind.StormCollapseJudgment,
            new { bundle, kind, stormThreshold },
            now,
            matching.Select(item => item.Id).ToList(),
            dispositions,
            $"storm-collapse kind={kind} count={matching.Count} threshold={stormThreshold} collapse={judgment.ShouldCollapse}");

        return new StewardOutput<StewardStormCollapseJudgment>(judgment, receipt);
    }

    public StewardOutput<StewardDailyBacklogDigest> ComposeDailyBacklogDigest(
        StewardBriefingBundle bundle,
        IReadOnlyList<StewardBacklogCandidate> candidates,
        DateTimeOffset now)
    {
        var usedScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranked = candidates
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.PipelineUnits)
            .Select((item, index) =>
            {
                var scopeKeys = item.ScopeKeys
                    .Where(scope => !string.IsNullOrWhiteSpace(scope))
                    .Select(scope => scope.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var disjoint = scopeKeys.All(scope => !usedScopes.Contains(scope));
                foreach (var scope in scopeKeys)
                {
                    usedScopes.Add(scope);
                }

                return new StewardRankedBacklogItem(item.Id, item.Title, index + 1, item.PipelineUnits, disjoint);
            })
            .ToList();
        var digest = new StewardDailyBacklogDigest(now, ranked);
        var receipt = Receipt(
            StewardOutputKind.DailyBacklogDigest,
            new { bundle, candidates },
            now,
            candidates.Select(item => item.Id).ToList(),
            [],
            $"daily-backlog-digest items={ranked.Count}");

        return new StewardOutput<StewardDailyBacklogDigest>(digest, receipt);
    }

    public StewardOutput<StewardDailyBrief> ComposeDailyBrief(
        StewardBriefingBundle bundle,
        string next,
        decimal spend,
        DateTimeOffset now,
        IReadOnlyList<StewardShadowClassAgreementRate>? shadowAgreement = null)
    {
        var brief = new StewardDailyBrief(
            now,
            bundle.GoalTasks.Count(item => item.State.Equals("Landed", StringComparison.OrdinalIgnoreCase)),
            bundle.Escalations.Count(item => item.SeverityRank >= 3),
            next,
            spend,
            shadowAgreement);
        var receipt = Receipt(
            StewardOutputKind.DailyBrief,
            shadowAgreement is null
                ? new { bundle, next, spend } // Preserve hashes for briefs without shadow data.
                : (object)new { bundle, next, spend, shadowAgreement },
            now,
            bundle.Escalations.Select(item => item.Id).ToList(),
            [],
            $"daily-brief landed={brief.LandedGoals} humanBlocked={brief.HumanBlockedItems} spend={spend}");

        return new StewardOutput<StewardDailyBrief>(brief, receipt);
    }

    public StewardOutput<StewardCatchUpReplay> ComposeCatchUpReplay(
        StewardBriefingBundle bundle,
        DateTimeOffset from,
        DateTimeOffset to,
        StewardBoardStateDiff boardDiff,
        IReadOnlyList<StewardAutonomousAction> actions)
    {
        var replay = new StewardCatchUpReplay(
            from,
            to,
            boardDiff,
            actions
                .Where(item => item.OccurredAt >= from && item.OccurredAt <= to)
                .OrderBy(item => item.OccurredAt)
                .ToList());
        var receipt = Receipt(
            StewardOutputKind.CatchUpReplay,
            new { bundle, from, to, boardDiff, actions },
            to,
            replay.AutonomousActions.Select(item => item.ActionId).ToList(),
            replay.AutonomousActions
                .Select(item => StewardInboxDisposition.ReceiptLinkedAction(item.ActionId, item.ReceiptId))
                .ToList(),
            $"catch-up-replay actions={replay.AutonomousActions.Count}");

        return new StewardOutput<StewardCatchUpReplay>(replay, receipt);
    }

    internal static StewardTriageReceipt RawReceipt(
        StewardOutputKind kind,
        StewardBriefingBundle bundle,
        StewardEscalationItem escalation,
        string rawReceiptId,
        DateTimeOffset now,
        string summary) =>
        Receipt(
            kind,
            new { bundle, escalation },
            now,
            [escalation.Id],
            [StewardInboxDisposition.RaisedRaw(escalation.Id, rawReceiptId)],
            summary);

    private static StewardTriageReceipt Receipt(
        StewardOutputKind kind,
        object inputs,
        DateTimeOffset now,
        IReadOnlyList<string> inputIds,
        IReadOnlyList<StewardInboxDisposition> dispositions,
        string summary,
        StewardCardLoadMeasurement? measurement = null)
    {
        var hash = StewardInputHasher.Hash(inputs);
        var id = $"steward-{ControlPlaneDecisionCard.ComputeFingerprint($"{kind}|{hash}|{now:O}|{string.Join(',', inputIds)}|{summary}")}";
        return new StewardTriageReceipt(id, kind, hash, now, inputIds, dispositions, summary, measurement);
    }

    private static string RenderDecisionCardBody(
        StewardBriefingBundle bundle,
        StewardPrecedentMatch? precedent)
    {
        var receipts = bundle.Receipts;
        var lines = new List<string>
        {
            $"trx={receipts.TrxCount} failedTrx={receipts.FailedTrxCount} exitCodes={string.Join(',', receipts.ExitCodes)}",
            $"diffFiles={receipts.DiffFiles} insertions={receipts.DiffInsertions} deletions={receipts.DiffDeletions}",
            $"policy={bundle.PolicySnapshot.AutonomyPolicyName} denylist={bundle.PolicySnapshot.Denylist.Count}",
            $"interruptBudget={bundle.InterruptBudget.Used}/{bundle.InterruptBudget.DailyBudget} remaining={bundle.InterruptBudget.Remaining}",
            precedent is null
                ? "precedent=novel"
                : $"precedent={precedent.Kind}:{precedent.CauseFingerprint} prior={precedent.PriorCount} resolved={precedent.ResolvedCount}"
        };

        lines.AddRange(bundle.FailingRoundProvenance.Select(item =>
            $"provenance={item.Stage}:{item.ReceiptId}:exit={item.ExitCode}"));
        lines.AddRange(bundle.WorkerProse.Select(item =>
            $"worker-prose[{item.Label}] quoted: \"{item.Quote}\""));
        return string.Join('\n', lines);
    }
}
