namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record StewardPrecedentKey(string Kind, string CauseFingerprint);

public sealed record StewardBriefingBundleInputs(
    IReadOnlyList<StewardEscalationItem> Escalations,
    IReadOnlyList<StewardGoalTaskRecord> GoalTasks,
    StewardNumericReceiptSummary Receipts,
    IReadOnlyDictionary<StewardPrecedentKey, StewardPrecedentMatch> MatchedPrecedents,
    StewardPolicySnapshot PolicySnapshot,
    StewardInterruptBudgetLedger InterruptBudget,
    IReadOnlyList<StewardProvenanceLink> FailingRoundProvenance,
    IReadOnlyList<StewardQuotedWorkerProse> WorkerProse);

public sealed class StewardBriefingBundleProducer
{
    public StewardBriefingBundle Produce(StewardBriefingBundleInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ValidatePrecedentKeys(inputs.MatchedPrecedents);

        return new StewardBriefingBundle(
            inputs.Escalations.ToList(),
            inputs.GoalTasks.ToList(),
            inputs.Receipts,
            inputs.MatchedPrecedents.Values.ToList(),
            inputs.PolicySnapshot,
            inputs.InterruptBudget,
            inputs.FailingRoundProvenance.ToList(),
            inputs.WorkerProse.ToList());
    }

    private static void ValidatePrecedentKeys(
        IReadOnlyDictionary<StewardPrecedentKey, StewardPrecedentMatch> precedents)
    {
        foreach (var (key, match) in precedents)
        {
            if (!key.Kind.Equals(match.Kind, StringComparison.OrdinalIgnoreCase) ||
                !key.CauseFingerprint.Equals(match.CauseFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Precedent keys must match the precedent kind and cause fingerprint.",
                    nameof(precedents));
            }
        }
    }
}
