using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillHost
{
    internal static ConductorBoardFillHost CreateDefault(OrchestratorWorkspace workspace,
        Func<ConductorAutonomyPolicy> policy, Func<string?>? mainHead = null) => new(
        new(ConductorBoardFillDraftStore.DefaultPath(workspace)),
        (id, token) => AuthorBriefDraftService.Run(id, workspace,
            new(WorkerProcessRunner.RunBufferedAsync, new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory, workspace.IntegrationBranch),
                ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath)),
            TextWriter.Null, TextWriter.Null, token, "board-fill-drafts"),
        () => BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath),
        (kernel, items) =>
        {
            var byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            return item => BacklogDependencyReadiness.Evaluate(item,
                id => kernel.Goals.FirstOrDefault(goal => goal.Id.Value == id),
                id => byId.GetValueOrDefault(id),
                id =>
                {
                    try
                    {
                        var owner = claims.ResolveClaim(kernel, id);
                        return new(kernel.Goals.FirstOrDefault(goal => goal.Id.Value == owner?.OwnerGoalId));
                    }
                    catch (LegacySourceBacklogOwnerAmbiguousException) { return new(null, true); }
                },
                goal => LandingState(workspace, goal));
        }, policy, new(workspace.ConductEventsLogPath), verifier: new BoardFillPremiseVerifier(
            () => ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath),
            new GitAuthorBriefDraftRepository(workspace.ExecutionDirectory, workspace.IntegrationBranch), workspace.ExecutionDirectory),
        filing: CreateFilingSeams(workspace), mainHead: mainHead);

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
