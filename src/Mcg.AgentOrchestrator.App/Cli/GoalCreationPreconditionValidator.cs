using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalCreationPreconditionValidator
{
    private const string ReasonDataKey = "Mcg.GoalCreationPreconditionReason";

    internal static void Validate(
        AgentOrchestratorKernel currentKernel,
        GoalSnapshot preparedSnapshot,
        OrchestratorWorkspace workspace,
        string? replacementPredecessorGoalId = null)
    {
        if (currentKernel.Goals.Any(goal => goal.Id.Value == preparedSnapshot.Id))
            throw Changed("prepared-goal-id-exists", ("goal", preparedSnapshot.Id));

        var preparedDependencies = (preparedSnapshot.DependsOn ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (preparedDependencies.Any(id => currentKernel.Goals.All(goal => goal.Id.Value != id)))
            throw Changed("dependency-target-missing", ("goal", preparedSnapshot.Id));

        if (preparedSnapshot.SourceBacklogItemId is not { } backlogItemId)
            return;

        var backlogItem = new BacklogStore(workspace.BacklogStorePath)
            .GetByIdPrefixAsync(backlogItemId)
            .GetAwaiter()
            .GetResult();
        if (backlogItem is null || !string.Equals(backlogItem.Id, backlogItemId, StringComparison.Ordinal))
            throw Changed("source-backlog-missing", ("backlogItem", backlogItemId));

        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        SourceBacklogClaimSnapshot claim;
        try
        {
            claim = replacementPredecessorGoalId is null
                ? claimStore.EnsureClaimForNewGoal(
                    currentKernel,
                    backlogItemId,
                    preparedSnapshot.Id,
                    preparedSnapshot.SourceBacklogCoverage ?? SourceBacklogCoverage.Full)
                : claimStore.ResolveOrMaterializeClaim(currentKernel, backlogItemId);
        }
        catch (LegacySourceBacklogOwnerAmbiguousException ambiguous)
        {
            throw Changed(
                "legacy-owner-ambiguous",
                ("backlogItem", backlogItemId),
                ("linkedGoals", string.Join(',', ambiguous.LinkedGoalIds)));
        }

        if (!string.Equals(claim.OwnerGoalId, preparedSnapshot.Id, StringComparison.Ordinal) &&
            replacementPredecessorGoalId is not null &&
            !string.Equals(claim.OwnerGoalId, replacementPredecessorGoalId, StringComparison.Ordinal))
        {
            throw Changed(
                "source-backlog-consumed",
                ("backlogItem", backlogItemId),
                ("competingGoal", claim.OwnerGoalId),
                ("claimVersion", claim.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("replacement", "goal-replace"));
        }

        if (!string.Equals(claim.OwnerGoalId, preparedSnapshot.Id, StringComparison.Ordinal) &&
            replacementPredecessorGoalId is null)
        {
            var owner = currentKernel.Goals.FirstOrDefault(goal => goal.Id.Value == claim.OwnerGoalId);
            if (owner is null)
            {
                throw Changed(
                    "source-backlog-owner-unresolved",
                    ("backlogItem", backlogItemId),
                    ("competingGoal", claim.OwnerGoalId),
                    ("claimVersion", claim.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            var facts = CliCommandHandlers.BuildSourceBacklogSiblingFacts(
                workspace.ExecutionDirectory,
                owner,
                claim.Coverage,
                workspaceExists: false);
            if (SourceBacklogSiblingAdmissionPolicy.Evaluate(facts) == SourceBacklogSiblingAdmission.RefuseActiveOwnerFullCoverage)
            {
                throw Changed(
                    SourceBacklogSiblingAdmissionPolicy.ActiveOwnerFullCoverageReason,
                    ("backlogItem", backlogItemId),
                    ("competingGoal", claim.OwnerGoalId),
                    ("claimVersion", claim.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("ownerStatus", facts.OwnerStatus.ToString()),
                    ("ownerState", facts.OwnerLifecycleState.ToString()),
                    ("coverage", facts.Coverage.ToString().ToLowerInvariant()));
            }
        }

        var currentDependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in backlogItem.Dependencies)
        {
            var dependencyGoal = dependency.TargetKind == BacklogDependencyTargetKind.Goal
                ? currentKernel.Goals.FirstOrDefault(goal => goal.Id.Value == dependency.PrerequisiteId)
                : ResolveAuthoritativeGoal(currentKernel, claimStore, dependency.PrerequisiteId);
            if (dependencyGoal is null)
            {
                throw Changed(
                    "dependency-target-missing",
                    ("backlogItem", backlogItemId),
                    ("dependency", dependency.PrerequisiteId));
            }

            currentDependencies.Add(dependencyGoal.Id.Value);
        }

        if (!currentDependencies.SetEquals(preparedDependencies))
        {
            throw Changed(
                "source-backlog-dependencies-changed",
                ("backlogItem", backlogItemId));
        }
    }

    internal static InvalidOperationException Changed(
        string reason,
        params (string Key, string Value)[] context)
    {
        var suffix = context.Length == 0
            ? string.Empty
            : " " + string.Join(' ', context.Select(item => $"{item.Key}={item.Value}"));
        var exception = new InvalidOperationException(
            $"GOAL_CREATE_PRECONDITION_CHANGED reason={reason}{suffix}");
        exception.Data[ReasonDataKey] = reason;
        return exception;
    }

    internal static string? GetReason(Exception exception) =>
        exception.Data[ReasonDataKey] as string;

    private static Goal? ResolveAuthoritativeGoal(
        AgentOrchestratorKernel kernel,
        SourceBacklogClaimStore claimStore,
        string backlogItemId)
    {
        var claim = claimStore.ResolveClaim(kernel, backlogItemId);
        return claim is null
            ? null
            : kernel.Goals.FirstOrDefault(goal => goal.Id.Value == claim.OwnerGoalId);
    }
}
