using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillHost
{
    internal static ConductorBoardFillHost CreateDefault(OrchestratorWorkspace workspace,
        Func<ConductorAutonomyPolicy> policy, Func<string?>? mainHead = null,
        Func<string, CancellationToken, AuthorBriefDraftOutcome>? draft = null) => new(
        new(ConductorBoardFillDraftStore.DefaultPath(workspace)),
        draft ?? ((id, token) => AuthorBriefDraftService.Run(id, workspace,
            new(WorkerProcessRunner.RunBufferedAsync, new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory, workspace.IntegrationBranch),
                ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath)),
            TextWriter.Null, TextWriter.Null, token, "board-fill-drafts", includeBoardFillMetadata: true)),
        () => BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath),
        (kernel, items) =>
        {
            var byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var dependencyClaims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            EpicPlanGoalClaims claims;
            try { claims = EpicPlanGoalClaimSnapshot.Read(workspace.SqliteStatePath, kernel.Goals.ToArray()); }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                return _ => new(false, false, " [Blocked: source ownership unavailable]");
            }
            return item =>
            {
                if (!claims.Resolve(item).AutomaticIntakeEligible)
                    return new(false, false, " [Blocked: source already claimed or unresolved]");
                // A malformed prerequisite must block its dependent, not abort selection of independent work.
                if (item.Dependencies.Any(dependency => dependency.TargetKind == BacklogDependencyTargetKind.Backlog &&
                    byId.TryGetValue(dependency.PrerequisiteId, out var prerequisite) &&
                    claims.Resolve(prerequisite) is { OwnerGoalId: not null, Coverage: null }))
                    return new(false, false, " [Blocked: prerequisite ownership unavailable]");
                return BacklogDependencyReadiness.Evaluate(item,
                id => kernel.Goals.FirstOrDefault(goal => goal.Id.Value == id),
                id => byId.GetValueOrDefault(id),
                id =>
                {
                    try
                    {
                        var owner = dependencyClaims.ResolveClaim(kernel, id);
                        return new(kernel.Goals.FirstOrDefault(goal => goal.Id.Value == owner?.OwnerGoalId));
                    }
                    catch (LegacySourceBacklogOwnerAmbiguousException) { return new(null, true); }
                },
                goal => LandingState(workspace, goal));
            };
        }, policy, new(workspace.ConductEventsLogPath), verifier: new BoardFillPremiseVerifier(
            () => ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath),
            new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory, workspace.IntegrationBranch), workspace.ExecutionDirectory),
        filing: CreateFilingSeams(workspace), mainHead: mainHead,
        trackedEdits: () => GitTrackedEditsProbe.Probe(workspace.ExecutionDirectory),
        staleMainHead: mainHead ?? (() => BoardFillStaleItemRule.ReadMainHead(workspace.ExecutionDirectory, workspace.IntegrationBranch)),
        preferredOrder: () => EpicPlanReadySliceSelector.Read(workspace),
        stalledEpics: new StalledEpicAttention(() => StalledEpicAttention.ReadCandidates(workspace),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory), new(workspace.ConductEventsLogPath)));

    private static BoardFillFilingSeams CreateFilingSeams(OrchestratorWorkspace workspace)
    {
        var intake = new BoardFillCliGoalIntake(workspace);
        return new(intake, intake.ReadBoard, () =>
        {
            var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify", $"{workspace.IntegrationBranch}^{{commit}}");
            return result.Succeeded ? result.Output.Trim() : null;
        });
    }

    private static string LandingState(OrchestratorWorkspace workspace, Goal goal)
    {
        var journal = GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id);
        return GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(
            GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id) is not null,
            IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(journal),
            IsRecorded: journal.LatestByOperation.Any(entry =>
                entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase) &&
                entry.Status == GoalOperationStatus.Completed),
            IsCleanedUp: journal.LatestByOperation.Any(entry =>
                entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) &&
                entry.Status == GoalOperationStatus.Completed))).ToString();
    }
}
