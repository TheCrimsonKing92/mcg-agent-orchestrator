using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static LandingExecutorTests;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsDenylistOwnerHold
{
    internal const string Protected = "src/Mcg.AgentOrchestrator.Core/Conductor/ConductorAutonomyPolicy.cs";
    private const string Ordinary = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/LandingDenylistProbe.cs";

    [Fact]
    public void Execute_UnapprovedContent_HoldsRefsAndDeduplicatesUntilApproval()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, Protected);
            var main = ReadGit(repo, "rev-parse", "main");
            Git(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            var sha = ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var prefix = goal.Id.Value[..8];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var held = LandingExecutor.Execute(kernel, goal, workspace,
                    policy: ConductorAutonomyPolicy.Permissive, eventWriter: writer);
                Assert.False(held.MainAdvanced);
                Assert.StartsWith("landing mutation blocked: landing-denylist owner approval required",
                    Assert.IsType<LandingDecision.Escalate>(held.Decision).Reason);
                Assert.Equal(main, ReadGit(repo, "rev-parse", "main"));
                Assert.Equal(main, ReadGit(repo, "rev-parse", LandingExecutor.IntegrationBranchName));
                AssertNoLanded(workspace, goal.Id);
                var item = Assert.Single(decisions.GetAttentionQueueAsync().GetAwaiter().GetResult()
                    .Where(item => item.GoalId == goal.Id.Value && item.Type == CollaborationItemType.Decision));
                Assert.False(CollaborationItemLifecycle.IsTerminal(item.Status));
                Assert.Contains("rule=landing-policy", item.Body, StringComparison.Ordinal);
                Assert.Contains($"path={Protected}", item.Body, StringComparison.Ordinal);
                Assert.Contains("fingerprint=v1:", item.Body, StringComparison.Ordinal);
                Assert.Contains($"approve-policy-change {prefix} {sha}", item.Body, StringComparison.Ordinal);
            }
            Approve(workspace, goal, sha);
            var landed = LandingExecutor.Execute(kernel, goal, workspace,
                policy: ConductorAutonomyPolicy.Permissive, eventWriter: writer);
            Assert.True(landed.MainAdvanced);
            Assert.Equal(landed.MergeCommitSha, ReadGit(repo, "rev-parse", "main"));
            using var line = ReadLanded(workspace, goal.Id);
            Assert.Equal("denylist-match-owner-approved", line.RootElement.GetProperty("admissionRule").GetString());
            Assert.Equal([$"landing-policy={Protected}"], ReadMatches(line));
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Execute_HistoricalApproval_RequiresSameGoalAndProtectedBlobs(
        bool changeProtected, bool otherGoal, bool advances)
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, Protected);
            var worktree = GoalWorktrees.WorktreePath(repo, goal.Id);
            var approvedSha = ReadGit(worktree, "rev-parse", "HEAD");
            AppendCommit(worktree, changeProtected ? Protected : Ordinary, "second revision");
            var head = ReadGit(worktree, "rev-parse", "HEAD");
            Assert.NotEqual(approvedSha, head);
            var approvalGoal = otherGoal ? CreateVerifiedGoal(repo).Goal : goal;
            Approve(workspace, approvalGoal, approvedSha);
            GoalOperationJournal.AcceptancePassed(repo, goal, "conductor:acceptance", head,
                ReadGit(repo, "rev-parse", "main"), "passed new head", DateTimeOffset.UtcNow.AddMinutes(-1));
            var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            Assert.False(AcceptancePolicyChangeDecision.IsApprovedAsync(decisions, goal.Id.Value, head).GetAwaiter().GetResult());
            var result = LandingExecutor.Execute(kernel, goal, workspace,
                policy: ConductorAutonomyPolicy.Permissive,
                eventWriter: new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory));
            Assert.Equal(advances, result.MainAdvanced);
            if (advances)
            {
                using var line = ReadLanded(workspace, goal.Id);
                Assert.Equal("denylist-match-owner-approved", line.RootElement.GetProperty("admissionRule").GetString());
                Assert.Equal([$"landing-policy={Protected}"], ReadMatches(line));
            }
            else
            {
                Assert.StartsWith("landing mutation blocked: landing-denylist owner approval required",
                    Assert.IsType<LandingDecision.Escalate>(result.Decision).Reason);
                AssertNoLanded(workspace, goal.Id);
            }
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Fact]
    public void Execute_OrdinaryContent_LandsWithoutApprovalOrAttention()
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, Ordinary);
            var result = LandingExecutor.Execute(kernel, goal, workspace,
                policy: ConductorAutonomyPolicy.Permissive,
                eventWriter: new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory));
            Assert.True(result.MainAdvanced);
            using var line = ReadLanded(workspace, goal.Id);
            Assert.Equal("green-gate-auto", line.RootElement.GetProperty("admissionRule").GetString());
            Assert.Empty(ReadMatches(line));
            Assert.Empty(CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync().GetAwaiter().GetResult());
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Fact]
    public void Execute_InvalidMainConfig_HoldsUnderDefaultUntilApproval()
    {
        var repo = CreateGitRepository();
        try
        {
            const string config = "config/landing-denylist.json";
            AppendCommit(repo, config, "invalid JSON");
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, config);
            var sha = ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var held = LandingExecutor.Execute(kernel, goal, workspace,
                policy: ConductorAutonomyPolicy.Permissive, eventWriter: writer);
            Assert.False(held.MainAdvanced);
            Assert.Contains("rule=landing-policy path=config/landing-denylist.json",
                Assert.IsType<LandingDecision.Escalate>(held.Decision).Reason, StringComparison.Ordinal);
            AssertNoLanded(workspace, goal.Id);
            Approve(workspace, goal, sha);
            Assert.True(LandingExecutor.Execute(kernel, goal, workspace,
                policy: ConductorAutonomyPolicy.Permissive, eventWriter: writer).MainAdvanced);
            using var line = ReadLanded(workspace, goal.Id);
            Assert.Equal("denylist-match-owner-approved", line.RootElement.GetProperty("admissionRule").GetString());
            Assert.StartsWith("invalid:", line.RootElement.GetProperty("landingDenylistSource").GetString());
            Assert.Equal([$"landing-policy={config}"], ReadMatches(line));
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Fact]
    public void ContentFingerprint_DuplicateAndMissingPaths_UsesOrdinalLfBytes()
    {
        var repo = CreateGitRepository();
        try
        {
            AppendCommit(repo, "z.txt", "protected");
            var revision = ReadGit(repo, "rev-parse", "HEAD");
            var blob = ReadGit(repo, "rev-parse", $"{revision}:z.txt");
            var bytes = Encoding.UTF8.GetBytes($"a.txt\tabsent\nz.txt\t{blob}\n");
            var expected = "v1:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expected, LandingDenylistOwnerGate.ContentFingerprint(repo, revision, ["z.txt", "a.txt", "z.txt"]));
            Assert.Throws<InvalidOperationException>(() =>
                LandingDenylistOwnerGate.ContentFingerprint(repo, new string('f', 40), ["a.txt"]));
        }
        finally { TryDeleteDirectory(repo); }
    }

    internal static void Approve(OrchestratorWorkspace workspace, Goal goal, string sha)
    {
        var now = DateTimeOffset.UtcNow;
        var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, goal.Id.Value, null, "{}", [], "owner", "cli", "local-process", now,
            ActorKind: OperatorActorKind.Human);
        OperatorIntentPolicyApproval.Apply(CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory), goal,
            intent, new ApprovePolicyChangeOperatorIntentPayload(sha, "Owner reviewed protected content."), now);
    }

    internal static void AssertNoLanded(OrchestratorWorkspace workspace, GoalId id)
    {
        var path = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{id.Value}.jsonl");
        if (File.Exists(path))
            Assert.DoesNotContain(File.ReadAllLines(path), line => line.Contains("\"eventType\":\"GoalLanded\"", StringComparison.Ordinal));
    }

    private static void Git(string repo, params string[] args) => Assert.Equal(0, GitCli.Run(repo, args).ExitCode);
}
