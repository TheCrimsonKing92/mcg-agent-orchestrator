using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class DispatchReadinessAssessmentCallSiteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameGoalAndScope_AllCallSitesAgree(bool held)
    {
        var (kernel, source, target, agents) = ReadinessTestSeed.Create(crossGoalHold: held);
        var profiles = WorkerProfileCatalog.Default();
        var assessment = DispatchReadinessAssessment.Evaluate(
            target, kernel.Goals, agents, profiles, ReadinessTestSeed.Now);
        var conductor = ConductorDriver.EvaluateConductorReadiness(
            kernel, target, agents, profiles, ReadinessTestSeed.Now);
        var report = GoalReadinessPreflight.Build(target, agents, "C:\\repo", profiles,
            resolveWorktree: (_, _) => "C:\\repo", providerHoldScope: kernel.Goals, now: ReadinessTestSeed.Now);
        var planner = CrossGoalSubscriptionStartPlanner.Build(kernel, agents, profiles, now: ReadinessTestSeed.Now);

        Assert.Equal(assessment.Verdict, conductor);
        if (held)
        {
            var blocked = Assert.IsType<DispatchReadinessBlocked>(conductor);
            Assert.True(blocked.HasCandidates);
            Assert.NotNull(blocked.ProviderBudgetHold);
            Assert.Equal(source.Id, blocked.ProviderBudgetHold.SourceGoalId);
            var finding = Assert.Single(report.Findings, item => item.Kind == "provider-budget-exhausted");
            Assert.Equal(blocked.Reason, finding.Message);
            Assert.Equal(GoalReadinessSeverity.Blocker, finding.Severity);
            Assert.False(finding.CanOverride);
            Assert.DoesNotContain(planner.Candidates, item => item.GoalId == target.Id.Value);
        }
        else
        {
            Assert.IsType<DispatchReadinessReady>(conductor);
            Assert.Single(report.Findings, item => item.Kind == "ready");
            Assert.DoesNotContain(report.Findings, item => item.Kind == "provider-budget-exhausted");
            var candidate = Assert.Single(planner.Candidates, item => item.GoalId == target.Id.Value);
            Assert.Equal(target.Tasks.Single().Id.Value, Assert.Single(candidate.TaskIds));
        }
    }

    [Fact]
    public void AppSource_OnlySharedAssessmentInvokesEvaluator()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App");
        var callers = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(app, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Where(path => File.ReadAllText(path).Contains("DispatchReadinessEvaluator.EvaluateDispatchReadiness(",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')).ToArray();
        Assert.Equal("src/Mcg.AgentOrchestrator.App/Orchestration/DispatchReadinessAssessment.cs", Assert.Single(callers));
        foreach (var file in new[] { "ConductorDriver.cs", "GoalReadinessPreflight.cs", "CrossGoalSubscriptionStartPlanner.cs" })
            Assert.Contains("DispatchReadinessAssessment.Evaluate(",
                File.ReadAllText(Path.Combine(app, "Orchestration", file)));
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (CliVerifiedRepositoryRoot.TryGetVerifiedRoot(out var root))
            return root;
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")) &&
                (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Repository root unavailable for {sourceFilePath}.");
    }
}
