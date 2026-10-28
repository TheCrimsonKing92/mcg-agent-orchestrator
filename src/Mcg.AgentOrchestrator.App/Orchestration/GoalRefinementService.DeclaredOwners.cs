using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class GoalRefinementService
{
    private static Dictionary<int, DeclaredEvidenceOwner> ClaimDeclaredEvidenceOwners(
        IReadOnlyList<string> acceptanceCriteria,
        List<string> operatorOwnedCriteria,
        List<string> acceptanceGateOwnedCriteria,
        Dictionary<int, string> claimedOwners)
    {
        var declared = new Dictionary<int, DeclaredEvidenceOwner>();
        for (var index = 0; index < acceptanceCriteria.Count; index++)
        {
            var owner = AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(acceptanceCriteria[index]);
            if (owner.Kind == DeclaredEvidenceOwnerKind.None)
                continue;

            declared.Add(index, owner);
            claimedOwners[index] = owner.WireToken;
            if (owner.Kind == DeclaredEvidenceOwnerKind.AcceptanceGate &&
                !acceptanceGateOwnedCriteria.Contains(acceptanceCriteria[index], StringComparer.Ordinal))
            {
                acceptanceGateOwnedCriteria.Add(acceptanceCriteria[index]);
            }
        }

        operatorOwnedCriteria.RemoveAll(criterion =>
            declared.ContainsKey(FindCriterionIndex(acceptanceCriteria, criterion)));
        return declared;
    }

    private static bool TryApplyDeclaredOwnerOverride(
        int criterionIndex,
        string requestedOwner,
        IReadOnlyDictionary<int, DeclaredEvidenceOwner> declared,
        HashSet<int> overriddenIndexes,
        List<string> diagnostics)
    {
        if (!declared.TryGetValue(criterionIndex, out var owner))
            return false;

        if (requestedOwner != owner.WireToken &&
            overriddenIndexes.Add(criterionIndex))
        {
            diagnostics.Add(
                $"Spec refiner owner overridden: declared criterion {criterionIndex + 1} declares {owner.WireToken}; refiner claimed {requestedOwner}");
        }

        return true;
    }

    private sealed record DeclaredOwnershipIssue(string Diagnostic, string? Criterion);

    private async Task<bool> AddDeclaredOwnershipQuestionsAsync(
        Goal goal,
        IReadOnlyList<DeclaredOwnershipIssue> issues,
        IReadOnlyList<RefinedSpecOpenQuestion> existingOpenQuestions,
        List<RefinedSpecOpenQuestion> openQuestions,
        CancellationToken cancellationToken)
    {
        var raisedClarification = false;
        foreach (var issue in issues)
        {
            const string forkKind = "ownership-lifecycle";
            var questionText = $"{issue.Diagnostic} Revise the versioned criterion or its declared_index to resolve this ownership issue. For an existing criterion, an explicit criterion-evidence-map repair may declare its evidence owner.";
            var normalizedKey = BuildNormalizedQuestionKey(forkKind, questionText);
            var topicKey = $"ownership-v{goal.AuthoritativeBrief.Version}-{normalizedKey[^16..]}";
            var existingOpen = existingOpenQuestions.FirstOrDefault(question =>
                string.Equals(ResolveQuestionTopicKey(question), topicKey, StringComparison.OrdinalIgnoreCase));
            if (existingOpen is not null)
            {
                AddOpenQuestionIfMissing(openQuestions, existingOpen);
                continue;
            }

            // A prior answer or a generic precedent cannot waive a still-present
            // contradictory or unmatched declaration in the current specification.
            var correlationKey = BuildCorrelationKey(goal.Id, topicKey);
            await _raiseCollaborationItem(
                CollaborationItemType.Clarification, goal.Id.Value,
                BuildClarificationSubject(topicKey),
                $"Question: {questionText}\nFork kind: {forkKind}\nTopic key: {topicKey}\nCriterion: {issue.Criterion}",
                correlationKey, cancellationToken);
            raisedClarification = true;
            openQuestions.Add(new RefinedSpecOpenQuestion(
                correlationKey, questionText, forkKind, "Open",
                TopicKey: topicKey, NormalizedQuestionKey: normalizedKey,
                Criterion: issue.Criterion, BlastRadius: "high"));
        }

        return raisedClarification;
    }

    private static bool HasExplicitCurrentOwnershipMapping(Goal goal, string criterion) =>
        goal.CriterionEvidenceObligations.Any(obligation =>
            obligation.CriterionVersion == goal.AuthoritativeRefinedSpecVersion?.Version &&
            string.Equals(obligation.Criterion, criterion, StringComparison.Ordinal) &&
            obligation.Owner is CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Operator &&
            obligation.Provenance.StartsWith("operator mapping by ", StringComparison.Ordinal));

    private static bool CanResolveDeclaredOwnershipQuestion(Goal goal, RefinedSpecOpenQuestion question)
    {
        if (question.TopicKey is not { } topic || !topic.StartsWith("ownership-v", StringComparison.Ordinal))
            return true;

        if (question.Criterion is { } criterion && HasExplicitCurrentOwnershipMapping(goal, criterion))
            return true;

        var versionText = topic["ownership-v".Length..].Split('-')[0];
        return int.TryParse(versionText, out var version) && goal.AuthoritativeBrief.Version > version &&
               goal.RefinedSpec is { } spec &&
               FindDeclaredOwnershipIssues(goal, AcceptanceCriteriaParser.ParseDeclared(goal.Objective),
                   spec.AcceptanceCriteria, [], spec.OperatorOwnedAcceptanceCriteria.ToList(),
                   spec.AcceptanceGateOwnedAcceptanceCriteria.ToList()).Count == 0;
    }

    private static List<DeclaredOwnershipIssue> FindDeclaredOwnershipIssues(
        Goal goal,
        IReadOnlyList<string> rawDeclaredCriteria,
        IReadOnlyList<string> criteria,
        IReadOnlyList<SpecRefinementCriterionOwnership> requestedOwners,
        List<string> operatorOwned,
        List<string> gateOwned)
    {
        var issues = new List<DeclaredOwnershipIssue>();
        foreach (var duplicate in rawDeclaredCriteria.GroupBy(text => text, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(new($"Declared criteria share the same identity text: '{duplicate.Key}'.", duplicate.Key));
        }

        for (var index = 0; index < criteria.Count; index++)
        {
            var criterion = criteria[index];
            if (HasExplicitCurrentOwnershipMapping(goal, criterion))
                continue;
            var marker = AcceptanceCriterionOwnershipMarker.Classify(criterion);
            if (AcceptanceCriterionOwnershipMarker.HasConflictingDeclaredEvidenceOwners(criterion))
            {
                issues.Add(new($"Declared criterion {index + 1} claims both Operator and Acceptance evidence ownership: '{criterion}'.", criterion));
                operatorOwned.RemoveAll(text => string.Equals(text, criterion, StringComparison.OrdinalIgnoreCase));
                gateOwned.RemoveAll(text => string.Equals(text, criterion, StringComparison.OrdinalIgnoreCase));
            }
            else if (marker.Classification == AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal &&
                     !operatorOwned.Contains(criterion, StringComparer.OrdinalIgnoreCase) &&
                     !gateOwned.Contains(criterion, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new($"Declared criterion {index + 1} is REAL-WORLD-DEPENDENT without an authoritative evidence owner: '{criterion}'.", criterion));
            }
        }

        if (rawDeclaredCriteria.Count == 0)
            return issues;

        var generatedClaims = new Dictionary<int, HashSet<string>>();
        foreach (var requested in requestedOwners)
        {
            var index = requested.DeclaredIndex is { } declaredIndex
                ? declaredIndex - 1
                : FindCriterionIndex(criteria, requested.Text);
            if (index < 0 || index >= criteria.Count)
            {
                if (requested.Owner != "worker")
                    issues.Add(new($"Owner claim '{requested.Owner}' for '{requested.Text}' has no unique declared criterion.", null));
                continue;
            }

            var criterion = criteria[index];
            // A generated discrepancy cannot make an unambiguous brief declaration
            // ambiguous. The producer already recorded the authoritative override.
            if (HasExplicitCurrentOwnershipMapping(goal, criterion) ||
                AcceptanceCriterionOwnershipMarker.ResolveDeclaredEvidenceOwner(criterion).Kind != DeclaredEvidenceOwnerKind.None ||
                AcceptanceCriterionOwnershipMarker.Classify(criterion).Classification == AcceptanceCriterionOwnershipClassification.OperatorOwned)
                continue;

            if (!generatedClaims.TryGetValue(index, out var claims))
                generatedClaims[index] = claims = new(StringComparer.Ordinal);
            claims.Add(requested.Owner);
        }

        foreach (var (index, claims) in generatedClaims.Where(pair => pair.Value.Count > 1))
        {
            issues.Add(new($"Declared criterion {index + 1} has conflicting generated owners ({string.Join(", ", claims.Order(StringComparer.Ordinal))}): '{criteria[index]}'.", criteria[index]));
            operatorOwned.RemoveAll(text => string.Equals(text, criteria[index], StringComparison.OrdinalIgnoreCase));
            gateOwned.RemoveAll(text => string.Equals(text, criteria[index], StringComparison.OrdinalIgnoreCase));
        }

        return issues;
    }
}
