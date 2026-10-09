using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SliceBatchAdmissionDecision(bool IsAllowed, string? Reason)
{
    internal static SliceBatchAdmissionDecision Allowed { get; } = new(true, null);
}

internal sealed class SliceBatchAdmissionEvaluator(
    Func<IReadOnlyCollection<Goal>> siblingSource,
    Func<Goal, IReadOnlyList<string>?> observedChangedPathsReader,
    Action<GoalId, string> policyDecisionRecorder,
    SliceBatchSiblingDependencyCoordinator? siblingDependencies = null)
{
    private const int MaximumScopeTextCharacters = 64_000;
    private readonly HashSet<GoalId> _admittedThisTick = [];
    private readonly Dictionary<GoalId, IReadOnlyList<string>?> _observedPathsThisTick = [];
    private readonly HashSet<string> _recordedPolicyDecisions = new(StringComparer.Ordinal);

    internal void BeginTick()
    {
        _admittedThisTick.Clear();
        _observedPathsThisTick.Clear();
    }

    internal SliceBatchAdmissionDecision Evaluate(Goal candidate)
    {
        if (candidate.SliceBatchParentId is null)
        {
            return SliceBatchAdmissionDecision.Allowed;
        }

        var siblings = siblingSource();
        var dependencyHold = SliceBatchSiblingDependencyCoordinator.TryDescribeSiblingHold(candidate, siblings);
        if (dependencyHold is not null)
        {
            RecordOnce(candidate.Id, dependencyHold);
            return new SliceBatchAdmissionDecision(false, dependencyHold);
        }

        var candidateDispatched = candidate.Tasks.Any(task => task.LastProcess is not null);
        if (!candidateDispatched)
        {
            var branchHold = (siblingDependencies ?? new SliceBatchSiblingDependencyCoordinator(null))
                .PrepareBranch(candidate, siblings);
            if (branchHold is not null)
            {
                RecordOnce(candidate.Id, branchHold);
                return new SliceBatchAdmissionDecision(false, branchHold);
            }
        }

        var candidateScope = GetEffectiveScope(candidate);
        var occupyingSiblings = GoalScopeCollisionAdvisor.SelectComparisonCandidates(siblings)
            .Where(goal =>
                goal.Id != candidate.Id &&
                goal.SliceBatchParentId == candidate.SliceBatchParentId &&
                (_admittedThisTick.Contains(goal.Id) ||
                  (goal.Tasks.Any(task => task.LastProcess is not null) &&
                   (!candidateDispatched ||
                    StringComparer.Ordinal.Compare(goal.Id.Value, candidate.Id.Value) < 0))));

        foreach (var sibling in occupyingSiblings)
        {
            if (SliceBatchSiblingDependencyCoordinator.IsSatisfiedSibling(candidate, sibling))
                continue;

            var siblingScope = GetEffectiveScope(sibling);
            var collision = candidateScope.IsAvailable && siblingScope.IsAvailable
                ? GoalScopeCollisionAdvisor.ClassifySiblingScopeCollision(candidateScope.Paths, siblingScope.Paths)
                : new SiblingScopeCollisionDecision(true, "scope-evidence-unavailable");
            if (!collision.HasCollision)
            {
                continue;
            }

            var siblingPrefix = sibling.Id.Value[..Math.Min(8, sibling.Id.Value.Length)];
            var reason = $"Slice-batch sibling {siblingPrefix} occupies colliding scope {collision.Evidence}.";
            RecordOnce(candidate.Id, reason);
            return new SliceBatchAdmissionDecision(false, reason);
        }

        return SliceBatchAdmissionDecision.Allowed;
    }

    internal void RecordAdmitted(Goal goal)
    {
        if (goal.SliceBatchParentId is not null)
        {
            _admittedThisTick.Add(goal.Id);
        }
    }

    private EffectiveScope GetEffectiveScope(Goal goal)
    {
        var declared = GoalFileScopeInference.FromGoal(
            goal,
            MaximumScopeTextCharacters,
            out var truncated);
        var declaredPaths = declared.Select(scope => scope.Path).ToArray();
        var observed = ReadObservedPaths(goal);
        if (observed is not null)
        {
            RecordDivergence(goal, declaredPaths, observed);
        }

        return new EffectiveScope(
            declaredPaths.Concat(observed ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            !truncated && declared.Any(scope => scope.IsTrusted));
    }

    private IReadOnlyList<string>? ReadObservedPaths(Goal goal)
    {
        if (_observedPathsThisTick.TryGetValue(goal.Id, out var cached))
        {
            return cached;
        }

        IReadOnlyList<string>? observed;
        try
        {
            observed = observedChangedPathsReader(goal);
        }
        catch (Exception exception)
        {
            RecordOnce(
                goal.Id,
                $"slice-scope-observation-unavailable goal={GoalPrefix(goal)} reason={exception.GetType().Name}");
            observed = null;
        }

        _observedPathsThisTick[goal.Id] = observed;
        return observed;
    }

    private void RecordDivergence(
        Goal goal,
        IReadOnlyList<string> declaredPaths,
        IReadOnlyList<string> observedPaths)
    {
        var declaredScope = RepositoryLandingScopeNormalization.Normalize(declaredPaths);
        foreach (var observedPath in observedPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(RepositoryPathOverlap.Normalize)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var observedOwnership = RepositoryOwnershipMap.Classify(observedPath);
            var observedResource = $"ownership:{observedOwnership.ReservationKey}";
            if (declaredPaths.Any(path => RepositoryPathOverlap.Overlaps(path, observedPath)) ||
                declaredScope.ResourceKeys.Contains(observedResource, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            RecordOnce(
                goal.Id,
                $"slice-scope-divergence goal={GoalPrefix(goal)} undeclared={observedPath} key={observedResource}");
        }
    }

    private void RecordOnce(GoalId goalId, string message)
    {
        var key = $"{goalId.Value}:{message}";
        if (_recordedPolicyDecisions.Add(key))
        {
            policyDecisionRecorder(goalId, message);
        }
    }

    private static string GoalPrefix(Goal goal) =>
        goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];

    private sealed record EffectiveScope(IReadOnlyList<string> Paths, bool IsAvailable);
}
