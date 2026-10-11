using System.Collections.Frozen;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Facts only: a snapshot never carries a runner or a per-index cherry memo.
internal sealed record GoalGitFactSnapshot(
    bool IsGitWorkTree,
    FrozenDictionary<string, string> GoalBranchTips,
    FrozenSet<string> MergedGoalBranches,
    FrozenSet<string> RegisteredWorktreePaths,
    string? MainSha,
    bool EvidenceAvailable);
