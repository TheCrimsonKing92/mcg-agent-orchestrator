using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record EpicProgressRollup(
    PortfolioEpic Epic,
    PortfolioProject? Project,
    IReadOnlyList<PortfolioEpicMember> Members,
    IReadOnlyList<EpicProgressMemberGoal> MemberGoals,
    int GoalCount,
    int BacklogItemCount,
    int ActiveCount,
    int VerifiedCount,
    int ParkedCount,
    int LandedCount,
    DateTimeOffset? NewestUpdatedAt,
    int VerifyingCount,
    int FailedCount,
    int ClosedCount,
    int MissingCount,
    int BacklogOpenCount,
    int BacklogDoneCount,
    int? WindowCreatedCount = null,
    int? WindowTransitionedCount = null,
    int? WindowFailedCount = null,
    int? WindowLandedCount = null);
