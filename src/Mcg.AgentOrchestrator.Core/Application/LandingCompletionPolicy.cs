using System.Globalization;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public enum LandingCompletionAction { Proceed, Hold, Escalate }

/// <summary>Post-gate observations; null means the driver has not reached that check.</summary>
public sealed record LandingCompletionFacts
{
    public bool? ApparatusRun { get; init; }
    public string? BranchSha { get; init; }
    public string? MainSha { get; init; }
    public string? ApparatusCandidate { get; init; }
    public bool? RunPassed { get; init; }
    public bool? TimedOut { get; init; }
    public string? FailureTail { get; init; }
    public int? UnmetCriteriaCount { get; init; }
    public string? UnmetCriteria { get; init; }
    public bool? RetryTaskAvailable { get; init; }
    public int? RetryCount { get; init; }
    public int? RetryBudget { get; init; }
    public string? EvidenceHoldReason { get; init; }
    public GoalLifecycleState? EvidenceHoldState { get; init; }
    public string? EvidenceHoldIdentity { get; init; }
    public string? MutationBlockReason { get; init; }
    public string? LandingResultKind { get; init; }
    public string? LandingReason { get; init; }

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("apparatusRun", Encode(ApparatusRun)), new("branchSha", BranchSha ?? ""),
        new("mainSha", MainSha ?? ""), new("apparatusCandidate", ApparatusCandidate ?? ""),
        new("runPassed", Encode(RunPassed)), new("timedOut", Encode(TimedOut)),
        new("failureTail", FailureTail ?? ""), new("unmetCriteriaCount", Encode(UnmetCriteriaCount)),
        new("unmetCriteria", UnmetCriteria ?? ""), new("retryTaskAvailable", Encode(RetryTaskAvailable)),
        new("retryCount", Encode(RetryCount)), new("retryBudget", Encode(RetryBudget)),
        new("evidenceHoldReason", EvidenceHoldReason ?? ""), new("evidenceHoldState", EvidenceHoldState?.ToString() ?? ""),
        new("evidenceHoldIdentity", EvidenceHoldIdentity ?? ""), new("mutationBlockReason", MutationBlockReason ?? ""),
        new("landingResultKind", LandingResultKind ?? ""), new("landingReason", LandingReason ?? "")
    ]);

    public static LandingCompletionFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 18)
            throw new InvalidOperationException("Landing completion replay requires exactly eighteen named facts.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
            if (!values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Duplicate landing completion fact '{fact.Name}'.");
        string Read(string name) => values.TryGetValue(name, out var value) ? value
            : throw new InvalidOperationException($"Missing landing completion fact '{name}'.");
        bool? ReadBool(string name) => Read(name) switch
        {
            "true" => true, "false" => false, "" => null,
            _ => throw new InvalidOperationException($"Invalid boolean landing completion fact '{name}'.")
        };
        int? ReadCount(string name)
        {
            var value = Read(name);
            if (value == "") return null;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                throw new InvalidOperationException($"Invalid count landing completion fact '{name}'.");
            return count;
        }
        var stateText = Read("evidenceHoldState");
        GoalLifecycleState? state = null;
        if (stateText != "")
        {
            if (!Enum.TryParse<GoalLifecycleState>(stateText, out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != stateText)
                throw new InvalidOperationException("Invalid landing completion evidence hold state fact.");
            state = parsed;
        }
        var kind = Read("landingResultKind");
        ValidateLandingKind(kind);
        return new()
        {
            ApparatusRun = ReadBool("apparatusRun"), BranchSha = NullIfEmpty(Read("branchSha")),
            MainSha = NullIfEmpty(Read("mainSha")), ApparatusCandidate = Read("apparatusCandidate"),
            RunPassed = ReadBool("runPassed"), TimedOut = ReadBool("timedOut"), FailureTail = Read("failureTail"),
            UnmetCriteriaCount = ReadCount("unmetCriteriaCount"), UnmetCriteria = Read("unmetCriteria"),
            RetryTaskAvailable = ReadBool("retryTaskAvailable"), RetryCount = ReadCount("retryCount"), RetryBudget = ReadCount("retryBudget"),
            EvidenceHoldReason = NullIfEmpty(Read("evidenceHoldReason")), EvidenceHoldState = state,
            EvidenceHoldIdentity = NullIfEmpty(Read("evidenceHoldIdentity")), MutationBlockReason = Read("mutationBlockReason"),
            LandingResultKind = kind, LandingReason = Read("landingReason")
        };
    }

    internal static void ValidateLandingKind(string? kind)
    {
        if (kind is not (null or "" or "promote" or "escalate" or "mutation-hold" or "ownership-hold"))
            throw new InvalidOperationException("Invalid landing completion result kind fact.");
    }

    private static string Encode(bool? value) => value switch { true => "true", false => "false", null => "" };
    private static string Encode(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string? NullIfEmpty(string value) => value == "" ? null : value;
}

public sealed record LandingCompletionDecision(
    LandingCompletionAction Action, int DiscriminatingRung, string DiscriminatingEvidence,
    string Reason, string? StableIdentity, ConductorEscalationKind? EscalationKind, LandingCompletionFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(LandingCompletionPolicy.StageName, Action.ToString(),
        DiscriminatingRung, DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects post-gate landing outcomes without owning driver effects.</summary>
public static class LandingCompletionPolicy
{
    public const string StageName = "landing-completion";

    public static LandingCompletionDecision Evaluate(LandingCompletionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        LandingCompletionFacts.ValidateLandingKind(facts.LandingResultKind);
        LandingCompletionDecision Decision(LandingCompletionAction action, int rung, string evidence, string reason,
            string? identity = null, ConductorEscalationKind? kind = null) => new(action, rung, evidence, reason, identity, kind, facts);
        static string Required(string? value, string name) => value ??
            throw new InvalidOperationException($"Landing completion requires {name}.");

        if (facts.ApparatusRun == true)
            return Decision(LandingCompletionAction.Hold, 1, "environmental-apparatus-hold",
                "Acceptance gate apparatus/environmental failure recorded for unchanged candidate " +
                $"{Required(facts.ApparatusCandidate, "a formatted candidate")}. Acceptance will not re-run until " +
                "the candidate or main HEAD changes, or an operator confirms acceptance-retry; no worker was reopened.",
                $"acceptance-apparatus:{facts.BranchSha ?? "unknown"}:{facts.MainSha ?? "unknown"}");
        if (facts.RunPassed == false && facts.UnmetCriteriaCount == 0)
        {
            if (facts.TimedOut is null)
                throw new InvalidOperationException("Landing completion requires a timeout observation.");
            return facts.TimedOut == true
                ? Decision(LandingCompletionAction.Escalate, 2, "acceptance-verification-timed-out",
                    "Acceptance verification timed out; rerun acceptance after clearing the blocker." + Required(facts.FailureTail, "a failure tail"))
                : Decision(LandingCompletionAction.Escalate, 3, "acceptance-verification-failed",
                    "Acceptance verification failed; review and fix before landing." + Required(facts.FailureTail, "a failure tail"),
                    kind: ConductorEscalationKind.AcceptanceVerificationFailed);
        }
        if (facts.RunPassed == true && facts.EvidenceHoldState is not null)
            return Decision(LandingCompletionAction.Hold, 4, "criterion-evidence-hold",
                Required(facts.EvidenceHoldReason, "an evidence hold reason"), facts.EvidenceHoldIdentity);
        if (facts.UnmetCriteriaCount > 0)
        {
            var criteria = Required(facts.UnmetCriteria, "formatted unmet criteria");
            if (facts.RetryTaskAvailable == false)
                return Decision(LandingCompletionAction.Escalate, 5, "unmet-criteria-no-retry-task",
                    $"Acceptance criteria unmet but no completed task is available to retry: {criteria}; review/land manually");
            if (facts.RetryTaskAvailable != true || facts.RetryCount is null || facts.RetryBudget is null)
                throw new InvalidOperationException("Landing completion requires retry task and budget observations.");
            if (facts.RetryCount < facts.RetryBudget)
                return Decision(LandingCompletionAction.Proceed, 6, "unmet-criteria-retry", "Acceptance criteria retry is within budget.");
            return Decision(LandingCompletionAction.Escalate, 7, "unmet-criteria-retries-exhausted",
                $"Acceptance criteria unmet after {facts.RetryCount} retries: {criteria}; review/land manually");
        }
        if (!string.IsNullOrWhiteSpace(facts.MutationBlockReason))
            return Decision(LandingCompletionAction.Hold, 8, "landing-mutation-boundary-hold",
                $"Landing held at mutation boundary: {facts.MutationBlockReason}");
        switch (facts.LandingResultKind)
        {
            case "mutation-hold":
                return Decision(LandingCompletionAction.Hold, 9, "landing-mutation-hold-escalation", Required(facts.LandingReason, "a landing reason"));
            case "ownership-hold":
                return Decision(LandingCompletionAction.Escalate, 10, "landing-ownership-hold", Required(facts.LandingReason, "a landing reason"));
            case "escalate":
                return Decision(LandingCompletionAction.Escalate, 11, "landing-escalation", Required(facts.LandingReason, "a landing reason"));
            case "promote":
                return Decision(LandingCompletionAction.Proceed, 0, "landing-promote", "Landing promoted.");
        }
        throw new InvalidOperationException("Landing completion facts do not identify an outcome.");
    }
}
