using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static LandingExecutorTests;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsLandingDenylist
{
    private const string Watched = "src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.cs";
    private const string Ordinary = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/LandingDenylistProbe.cs";
    private const string Config = "config/landing-denylist.json";
    private const string MainConfig = """{"version":1,"rules":[{"id":"watched","reason":"Observe","paths":["src/watched/**"]},{"id":"self","reason":"Observe config","paths":["config/landing-denylist.json"]}]}""";
    private const string CandidateConfig = """{"version":1,"rules":[{"id":"self","reason":"Observe config","paths":["config/landing-denylist.json"]}]}""";

    [Fact]
    public void MatchedAndUnmatchedCandidatesLandWithIdenticalOutcomes()
    {
        var matched = LandSingle(Watched);
        var unmatched = LandSingle(Ordinary);
        Assert.Equal(unmatched.MainAdvanced, matched.MainAdvanced);
        Assert.Equal(unmatched.Decision.GetType(), matched.Decision.GetType());
        var matchedDecision = Assert.IsType<LandingDecision.Promote>(matched.Decision);
        var unmatchedDecision = Assert.IsType<LandingDecision.Promote>(unmatched.Decision);
        Assert.Equal(unmatchedDecision.Decision!.DiscriminatingEvidence, matchedDecision.Decision!.DiscriminatingEvidence);
        Assert.Equal(unmatched.Message.Replace(GoalWorktrees.BranchName(new GoalId(unmatched.GoalId)), "goal/member"),
            matched.Message.Replace(GoalWorktrees.BranchName(new GoalId(matched.GoalId)), "goal/member"));
    }

    private static LandingResult LandSingle(string path)
    {
        var repo = CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = CreateAcceptedCandidate(repo, path);
            var sha = ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var result = LandingExecutor.Execute(kernel, goal, workspace, policy: ConductorAutonomyPolicy.Permissive, eventWriter: writer);
            Assert.True(result.MainAdvanced);
            Assert.Equal(result.MergeCommitSha, ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(sha, ReadGit(repo, "rev-parse", "main^2"));
            using var line = ReadLanded(workspace, goal.Id);
            Assert.Equal(result.MergeCommitSha, line.RootElement.GetProperty("admissionCandidateSha").GetString());
            Assert.Equal("built-in-default", line.RootElement.GetProperty("landingDenylistSource").GetString());
            Assert.Equal(path == Watched ? "denylist-match-recorded" : "green-gate-auto", line.RootElement.GetProperty("admissionRule").GetString());
            var matches = ReadMatches(line);
            if (path == Watched) Assert.Equal([$"process-launch={Watched}"], matches);
            else Assert.Empty(matches);
            return result;
        }
        finally { TryDeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MainConfigIsAuthoritativeAndInvalidConfigNeverHoldsLanding(bool invalid)
    {
        var repo = CreateGitRepository();
        try
        {
            AppendCommit(repo, Config, invalid ? "invalid JSON" : MainConfig);
            var main = ReadGit(repo, "rev-parse", "main");
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            Git(repo, "checkout", "-b", GoalWorktrees.BranchName(goal.Id));
            AppendCommit(repo, Config, CandidateConfig);
            AppendCommit(repo, "src/watched/a.txt", "candidate");
            var sha = ReadGit(repo, "rev-parse", "HEAD");
            Git(repo, "checkout", "main");
            GoalWorktrees.Ensure(repo, goal.Id);
            GoalOperationJournal.AcceptancePassed(repo, goal, "conductor:acceptance", sha, main, "passed", DateTimeOffset.UtcNow.AddMinutes(-1));
            var result = LandingExecutor.Execute(kernel, goal, workspace, policy: ConductorAutonomyPolicy.Permissive,
                eventWriter: new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory));
            Assert.True(result.MainAdvanced);
            Assert.Equal(result.MergeCommitSha, ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(sha, ReadGit(repo, "rev-parse", "main^2"));
            using var line = ReadLanded(workspace, goal.Id);
            Assert.Equal(result.MergeCommitSha, line.RootElement.GetProperty("admissionCandidateSha").GetString());
            if (invalid)
            {
                Assert.Equal("green-gate-auto", line.RootElement.GetProperty("admissionRule").GetString());
                Assert.StartsWith("invalid:", line.RootElement.GetProperty("landingDenylistSource").GetString());
                Assert.Empty(ReadMatches(line));
            }
            else
            {
                Assert.Equal("denylist-match-recorded", line.RootElement.GetProperty("admissionRule").GetString());
                Assert.Equal($"main:{main}", line.RootElement.GetProperty("landingDenylistSource").GetString());
                Assert.Equal(["watched=src/watched/a.txt", $"self={Config}"], ReadMatches(line));
            }
        }
        finally { TryDeleteDirectory(repo); }
    }

    private static void Git(string repo, params string[] args) => Assert.Equal(0, GitCli.Run(repo, args).ExitCode);

    internal static JsonDocument ReadLanded(OrchestratorWorkspace workspace, GoalId id)
    {
        var lines = File.ReadAllLines(Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{id.Value}.jsonl"));
        Assert.DoesNotContain(lines, text => text.Contains("\"eventType\":\"GoalEscalated\"", StringComparison.Ordinal));
        return JsonDocument.Parse(Assert.Single(lines.Where(text =>
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("eventType").GetString() == "GoalLanded";
        })));
    }

    internal static string[] ReadMatches(JsonDocument line) =>
        line.RootElement.GetProperty("landingDenylistMatches").EnumerateArray().Select(value => value.GetString()!).ToArray();
}
