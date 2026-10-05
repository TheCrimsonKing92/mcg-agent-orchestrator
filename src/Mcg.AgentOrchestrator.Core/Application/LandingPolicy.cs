using System.Globalization;

namespace Mcg.AgentOrchestrator.Core;

public enum LandingPolicyAction { Proceed, Escalate }

/// <summary>Pre-mutation observations; absent later facts have not been gathered yet.</summary>
public sealed record LandingFacts
{
    public bool? ChangedFilesResolved { get; init; }
    public string? ChangedFilesFailureReason { get; init; }
    public bool? AcceptanceAccepted { get; init; }
    public string? AcceptanceHoldDescription { get; init; }
    public bool? EvidenceRebindOutstanding { get; init; }
    public string? EvidenceDiagnostic { get; init; }
    public bool? OwnershipRequiresApproval { get; init; }
    public bool? AllowsAutonomousHighRiskOwnership { get; init; }
    public int? AttributableHoldRequestCount { get; init; }
    public string? IntegrationAncestry { get; init; }

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("changedFilesResolved", Encode(ChangedFilesResolved)),
        new("changedFilesFailureReason", ChangedFilesFailureReason ?? ""),
        new("acceptanceAccepted", Encode(AcceptanceAccepted)),
        new("acceptanceHoldDescription", AcceptanceHoldDescription ?? ""),
        new("evidenceRebindOutstanding", Encode(EvidenceRebindOutstanding)),
        new("evidenceDiagnostic", EvidenceDiagnostic ?? ""),
        new("ownershipRequiresApproval", Encode(OwnershipRequiresApproval)),
        new("allowsAutonomousHighRiskOwnership", Encode(AllowsAutonomousHighRiskOwnership)),
        new("attributableHoldRequestCount", AttributableHoldRequestCount?.ToString(CultureInfo.InvariantCulture) ?? ""),
        new("integrationAncestry", IntegrationAncestry ?? "")
    ]);

    public static LandingFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 10)
            throw new InvalidOperationException("Landing replay requires exactly ten named facts.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
            if (!values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Duplicate landing fact '{fact.Name}'.");
        string Read(string name) => values.TryGetValue(name, out var value) ? value
            : throw new InvalidOperationException($"Missing landing fact '{name}'.");
        bool? ReadBool(string name) => Read(name) switch
        {
            "true" => true, "false" => false, "" => null,
            _ => throw new InvalidOperationException($"Invalid boolean landing fact '{name}'.")
        };
        var countText = Read("attributableHoldRequestCount");
        int? count = null;
        if (countText != "")
        {
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                throw new InvalidOperationException("Invalid landing attributable hold request count fact.");
            count = parsed;
        }
        var ancestry = Read("integrationAncestry");
        ValidateAncestry(ancestry);
        return new()
        {
            ChangedFilesResolved = ReadBool("changedFilesResolved"),
            ChangedFilesFailureReason = Read("changedFilesFailureReason"),
            AcceptanceAccepted = ReadBool("acceptanceAccepted"),
            AcceptanceHoldDescription = Read("acceptanceHoldDescription"),
            EvidenceRebindOutstanding = ReadBool("evidenceRebindOutstanding"),
            EvidenceDiagnostic = Read("evidenceDiagnostic"),
            OwnershipRequiresApproval = ReadBool("ownershipRequiresApproval"),
            AllowsAutonomousHighRiskOwnership = ReadBool("allowsAutonomousHighRiskOwnership"),
            AttributableHoldRequestCount = count,
            IntegrationAncestry = ancestry
        };
    }

    internal static void ValidateAncestry(string? ancestry)
    {
        if (ancestry is not (null or "" or "absent" or "on-bound-main" or "diverged"))
            throw new InvalidOperationException("Invalid landing integration ancestry fact.");
    }

    private static string Encode(bool? value) => value switch { true => "true", false => "false", null => "" };
}

public sealed record LandingPolicyDecision(
    LandingPolicyAction Action, int DiscriminatingRung, string DiscriminatingEvidence,
    string Reason, LandingFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(LandingPolicy.StageName, Action.ToString(),
        DiscriminatingRung, DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects landing outcomes without owning executor effects.</summary>
public static class LandingPolicy
{
    public const string StageName = "landing";
    public const string OwnershipHoldReasonPrefix = "ownership-denylist hold";

    public static bool OwnershipHoldApplies(LandingFacts facts) =>
        facts.OwnershipRequiresApproval == true && facts.AllowsAutonomousHighRiskOwnership != true;

    public static LandingPolicyDecision Evaluate(LandingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        LandingFacts.ValidateAncestry(facts.IntegrationAncestry);
        if (facts.AttributableHoldRequestCount < 0)
            throw new InvalidOperationException("Invalid landing attributable hold request count fact.");
        LandingPolicyDecision Escalate(int rung, string evidence, string reason) =>
            new(LandingPolicyAction.Escalate, rung, evidence, reason, facts);
        static void RequireObserved(bool? fact)
        {
            if (fact is null)
                throw new InvalidOperationException("Landing facts do not identify an outcome.");
        }

        RequireObserved(facts.ChangedFilesResolved);
        if (facts.ChangedFilesResolved == false)
            return Escalate(1, "diff-scope-unknown", $"diff scope unknown: {facts.ChangedFilesFailureReason}");
        RequireObserved(facts.AcceptanceAccepted);
        if (facts.AcceptanceAccepted == false)
        {
            // The acceptance rung short-circuits before change risk or autonomy policy is consulted.
            // Empty changes and a null policy therefore reproduce the live decision from recorded facts alone.
            var engineDecision = LandingDecisionEngine.Decide(new LandingInputs(
                RepositoryChangeClassifier.Classify([]), AcceptancePassed: false,
                IntegrationToMainIsCleanFastForward: true, GoalFailureRetryCount: 0,
                Policy: null, AcceptanceHoldDescription: facts.AcceptanceHoldDescription));
            if (engineDecision is not LandingDecision.Escalate acceptanceDecision)
                throw new InvalidOperationException(
                    "Landing decision engine promoted a candidate whose acceptance status was not accepted.");
            return Escalate(2, "acceptance-not-accepted", acceptanceDecision.Reason);
        }
        RequireObserved(facts.EvidenceRebindOutstanding);
        if (facts.EvidenceRebindOutstanding == true)
            return Escalate(3, "criterion-evidence-rebind-outstanding", facts.EvidenceDiagnostic ??
                throw new InvalidOperationException("Landing requires an outstanding evidence diagnostic."));
        RequireObserved(facts.OwnershipRequiresApproval);
        if (OwnershipHoldApplies(facts))
        {
            var count = facts.AttributableHoldRequestCount ??
                throw new InvalidOperationException("Landing ownership hold requires an attributable hold request count.");
            if (count > 0)
                return Escalate(4, "ownership-hold",
                    $"{OwnershipHoldReasonPrefix}: {count} task(s) touched RequiresOperatorApproval path(s)");
            return Escalate(5, "ownership-hold-unattributable",
                "ownership-denylist diff touched RequiresOperatorApproval path(s), but no writing task attribution was available");
        }
        if (facts.IntegrationAncestry == "diverged")
            return Escalate(6, "integration-not-on-bound-main", "integration branch contains state not present on bound main");
        if (facts.IntegrationAncestry is "absent" or "on-bound-main")
            return new(LandingPolicyAction.Proceed, 0, "landing-proceed", "Landing pre-mutation checks passed.", facts);
        throw new InvalidOperationException("Landing facts do not identify an outcome.");
    }
}
