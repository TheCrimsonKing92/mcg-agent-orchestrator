using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

// Immutable evidence for one request, coherent within each independently written store.
internal sealed record EpicDetailReadSnapshot(EpicProgressRollup Row, DateTimeOffset? Since,
    IReadOnlyDictionary<string, Goal> Goals)
{
    internal static async Task<EpicDetailReadSnapshot> ReadAsync(OrchestratorWorkspace workspace,
        string reference, DateTimeOffset? since, CancellationToken cancellationToken,
        Action<string, SqliteConnection>? observe = null, bool includeReasons = true, bool includeDetailGoals = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(workspace.PortfolioStorePath))
            throw new InvalidOperationException($"Epic '{reference}' was not found.");
        using var portfolio = new SqliteReadSession(workspace.PortfolioStorePath, observe);
        var capture = await PortfolioStore.ReadEpicAsync(portfolio.Connection, workspace.PortfolioStorePath,
            reference, cancellationToken).ConfigureAwait(false);
        var backlogIds = capture.Members.Where(member => member.Kind == PortfolioMemberKind.BacklogItem)
            .Select(member => member.MemberId).Concat(capture.Plan.Items.Where(item => item.BacklogItemId is not null)
                .Select(item => item.BacklogItemId!)).Distinct(StringComparer.Ordinal).ToArray();
        using var backlogSession = File.Exists(workspace.BacklogStorePath) ? new SqliteReadSession(workspace.BacklogStorePath, observe) : null;
        var backlog = backlogSession is null ? [] : BoardFillBacklogSnapshot.ReadScoped(backlogSession.Connection, backlogIds, cancellationToken);
        var goalIds = capture.Members.Where(member => member.Kind == PortfolioMemberKind.Goal).Select(member => member.MemberId)
            .Concat(backlog.Where(item => backlogIds.Contains(item.Id, StringComparer.Ordinal)).SelectMany(item => item.Dependencies)
                .Where(dependency => dependency.TargetKind == BacklogDependencyTargetKind.Goal).Select(dependency => dependency.PrerequisiteId))
            .Distinct(StringComparer.Ordinal).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        using var state = File.Exists(workspace.SqliteStatePath) ? new SqliteReadSession(workspace.SqliteStatePath, observe) : null;
        var claims = state is null ? new EpicPlanGoalClaims(new Dictionary<string, EpicPlanSourceClaim>(),
            new Dictionary<string, EpicPlanLinkedGoal>(), new Dictionary<string, string>()) :
            EpicPlanGoalClaimSnapshot.ReadScoped(state.Connection, backlogIds, goalIds, cancellationToken);
        var metadata = state is null ? [] : await SqliteOrchestratorStateRepository.ReadEpicMetadataAsync(state.Connection,
            goalIds.Concat(claims.Goals.Keys), since is not null, cancellationToken).ConfigureAwait(false);
        var row = EpicProgressReadModel.Build([capture.Epic], capture.Project is null ? [] : [capture.Project],
            capture.Members, metadata, backlog, since).Single();
        var byBacklog = backlog.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var byGoal = metadata.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        var hydrationIds = includeDetailGoals ? row.MemberGoals.Where(member =>
                member.Bucket is EpicProgressBucket.Active or EpicProgressBucket.Verifying or EpicProgressBucket.Parked ||
                member.Bucket == EpicProgressBucket.Failed && (since is null || member.UpdatedAt >= since))
            .Select(member => member.Id) : Enumerable.Empty<string>();
        if (includeReasons)
            hydrationIds = hydrationIds.Concat(capture.Plan.Items.Where(item => item.BacklogItemId is not null && byBacklog.ContainsKey(item.BacklogItemId))
                .Select(item => claims.Resolve(byBacklog[item.BacklogItemId!]).OwnerGoalId)
                .Where(id => id is not null && byGoal.TryGetValue(id, out var goal) && goal.Status is "Failed" or "AcceptanceFailed" or "Parked")
                .Select(id => id!));
        var hydrated = state is null ? [] : await SqliteOrchestratorStateRepository.ReadEpicGoalsAsync(state.Connection,
            hydrationIds, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = EpicPlanStatusReader.Build(capture.Plan, capture.Members, byBacklog, byGoal, claims,
            includeReasons ? EpicPlanStatusReader.Reasons(hydrated) : null);
        return new(row with { Plan = plan }, since, hydrated.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal));
    }
}
