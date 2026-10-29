namespace Mcg.AgentOrchestrator.Core;

public enum CriterionEvidencePhase { PreLanding, PostLanding }
public enum CriterionObservationKind { RepositoryState, Runtime }

// One condition on one target; pre-landing proof belongs to a separate criterion.
public sealed record CriterionObservationScope(CriterionObservationKind Kind, string Condition, string Target)
{
    public bool IsValid => Enum.IsDefined(Kind) && !string.IsNullOrWhiteSpace(Condition) &&
        !string.IsNullOrWhiteSpace(Target);
}

public sealed record CriterionEvidenceDeclaration(
    string Criterion, CriterionEvidencePhase Phase = CriterionEvidencePhase.PreLanding,
    CriterionObservationScope? ObservationScope = null, bool Atomic = false);

// Activation is an attributed operator attestation, or the native publication fact
// for an explicitly repository-state scope. Submission time is never observation time.
public sealed record CriterionPostLandingObservation(
    string PromotedMainRevision, CriterionObservationScope Scope,
    string ActivationIdentity, string ActivationEvidence,
    DateTimeOffset ActivatedAt, DateTimeOffset ObservedAt,
    bool ActivationAttested = false, string? GoalId = null, string? ObligationId = null);

public static class CriterionEvidenceLifecycle
{
    public const string AtomicDeclarationRequired =
        "Separate atomic obligations and explicit phase declarations are required; PostLanding requires Operator ownership and a subsequent-observation condition and target.";

    internal static string? DescribeCompletionRefusal(Goal goal, bool fromMergeEvidence = false)
    {
        var outstandingObligations = goal.LandingPrerequisiteObligations;
        if (outstandingObligations.Count == 0) return null;
        var detail = string.Join(", ", outstandingObligations.Select(item =>
            $"{item.DisplayLabel}:{item.Owner}:{item.State}"));
        var source = fromMergeEvidence ? " from merge evidence" : "";
        return $"Goal '{goal.Id}' cannot complete{source} while criterion evidence obligations remain outstanding: {detail}.";
    }

    public static bool IsValid(CriterionEvidencePhase phase, CriterionEvidenceOwner owner,
        CriterionObservationScope? scope) => phase switch
        {
            CriterionEvidencePhase.PreLanding => scope is null,
            CriterionEvidencePhase.PostLanding => owner == CriterionEvidenceOwner.Operator && scope is { IsValid: true },
            _ => false
        };

    public static void Validate(RefinedSpec spec)
    {
        foreach (var declaration in spec.CriterionEvidenceDeclarations)
        {
            if (EvaluateDeclaration(spec, declaration) is { } rule)
                throw new ArgumentException(DescribeDeclarationFailure(declaration.Criterion, rule));
        }
    }

    public static string DescribeDeclarationFailure(string criterion, CriterionEvidenceDeclarationRule rule)
    {
        var name = rule switch
        {
            CriterionEvidenceDeclarationRule.CriterionCount => "criterion count",
            CriterionEvidenceDeclarationRule.DeclarationCount => "declaration count",
            CriterionEvidenceDeclarationRule.Owner => "owner",
            CriterionEvidenceDeclarationRule.PhaseAndScope => "phase and scope",
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
        return $"{AtomicDeclarationRequired} Offending criterion: '{criterion}'; failed rule: {name}.";
    }

    internal static bool IsValidDeclaration(RefinedSpec spec, CriterionEvidenceDeclaration declaration) =>
        EvaluateDeclaration(spec, declaration) is null;

    internal static CriterionEvidenceDeclarationRule? EvaluateDeclaration(
        RefinedSpec spec, CriterionEvidenceDeclaration declaration)
    {
        var criterion = declaration.Criterion;
        var owner = spec.OperatorOwnedAcceptanceCriteria.Count(item => item == criterion) == 1 &&
            !spec.AcceptanceGateOwnedAcceptanceCriteria.Contains(criterion)
            ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Unknown;
        if (spec.AcceptanceCriteria.Count(item => item == criterion) != 1)
            return CriterionEvidenceDeclarationRule.CriterionCount;
        if (spec.CriterionEvidenceDeclarations.Count(item => item.Criterion == criterion) != 1)
            return CriterionEvidenceDeclarationRule.DeclarationCount;
        if (declaration.Phase == CriterionEvidencePhase.PostLanding && owner != CriterionEvidenceOwner.Operator)
            return CriterionEvidenceDeclarationRule.Owner;
        if (!IsValid(declaration.Phase, owner, declaration.ObservationScope) ||
            declaration.Phase == CriterionEvidencePhase.PostLanding && !declaration.Atomic)
            return CriterionEvidenceDeclarationRule.PhaseAndScope;
        return null;
    }

    public static bool MatchesObservation(CriterionEvidenceObligation duty,
        CriterionPostLandingObservation observation, DateTimeOffset recordedAt)
    {
        if (!duty.IsValidatedPostLanding || duty.Publication is not { } publication ||
            observation.GoalId != publication.GoalId || observation.ObligationId != duty.Id ||
            observation.Scope != duty.ObservationScope ||
            !string.Equals(observation.PromotedMainRevision, publication.PromotedMainRevision, StringComparison.OrdinalIgnoreCase) ||
            observation.ActivatedAt < publication.PromotedAt || observation.ObservedAt <= observation.ActivatedAt ||
            observation.ObservedAt > recordedAt || string.IsNullOrWhiteSpace(observation.ActivationIdentity) ||
            string.IsNullOrWhiteSpace(observation.ActivationEvidence)) return false;
        return (observation.Scope.Kind == CriterionObservationKind.Runtime && observation.ActivationAttested) ||
            (observation.Scope.Kind == CriterionObservationKind.RepositoryState &&
            (observation.ActivationIdentity == publication.PromotedMainRevision &&
             observation.ActivationEvidence == publication.Provenance && observation.ActivatedAt == publication.PromotedAt));
    }
}
