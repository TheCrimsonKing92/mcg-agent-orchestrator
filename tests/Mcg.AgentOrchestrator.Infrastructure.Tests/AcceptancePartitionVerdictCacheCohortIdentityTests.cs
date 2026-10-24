using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its temporary journals, overrides and diagnostic sink.
public sealed class AcceptancePartitionVerdictCacheCohortIdentityTests
{
    private const string Lead = "11111111111111111111111111111111";
    private const string Other = "22222222222222222222222222222222";
    private const string Explicit = "33333333333333333333333333333333";

    [Fact]
    public void UnknownGoalWithMembersCachesUnderLowestMember()
    {
        using var context = new CacheContext();
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options, out var reason));

        Assert.Null(reason);
        Assert.Equal(Lead, cache.GoalId);
        Assert.Equal(Lead + ".jsonl", Path.GetFileName(cache.JournalPath));
        Assert.Equal($"PARTITION_VERDICT_CACHE identity=lead-member goal={Lead} members=2",
            Assert.Single(context.Diagnostics));

        IReadOnlyList<string>?[] invalidMemberships = [null, [], [Other, ""], [Other, " \t"], [Other, null!]];
        foreach (var members in invalidMemberships)
        {
            context.Diagnostics.Clear();
            Assert.Null(AcceptancePartitionVerdictCache.Create(
                context.Options with { MemberGoalIds = members }, out reason));
            Assert.Equal("goal-id-unknown", reason);
            Assert.Empty(context.Diagnostics);
        }

        context.Diagnostics.Clear();
        var explicitCache = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options with { GoalId = new GoalId(Explicit) }));
        Assert.Equal(Explicit, explicitCache.GoalId);
        Assert.Empty(context.Diagnostics);
        // An explicit goal retains precedence even over invalid membership.
        var explicitWithoutMembers = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options with
            {
                GoalId = new GoalId(Explicit), MemberGoalIds = [""]
            }));
        Assert.Equal(Explicit, explicitWithoutMembers.GoalId);
        Assert.Empty(context.Diagnostics);
    }

    [Fact]
    public void CohortGreenIsReusedOnlyForTheSameCombinedTree()
    {
        using var context = new CacheContext();
        var first = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options));
        Assert.Null(first.TryReuse(context.Lane));
        first.RecordExecution(context.Lane, new AcceptanceCheckResult(
            context.Lane.Name, true, 0, null, TestResultPaths: []));
        Assert.NotNull(first.CompleteAttempt());
        Assert.True(File.Exists(first.JournalPath));
        Assert.Equal(Lead + ".jsonl", Path.GetFileName(first.JournalPath));

        var second = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options with { ResolveAttemptId = () => "cohort-2" }));
        var reused = Assert.IsType<AcceptanceCheckResult>(second.TryReuse(context.Lane));
        Assert.True(reused.Passed);
        Assert.Contains("reuse_rule=pair", reused.ResultSummary);

        var solo = Assert.IsType<AcceptancePartitionVerdictCache>(
            AcceptancePartitionVerdictCache.Create(context.Options with
            {
                GoalId = new GoalId(Lead), MemberGoalIds = [Lead],
                ResolveCandidateTreeSha = _ => "member-tree", ResolveAttemptId = () => "solo-1",
                ResolveClosureHash = _ => null, ResolveDisjointLandingFacts = _ => null
            }));
        Assert.Equal(first.JournalPath, solo.JournalPath);
        Assert.Null(solo.TryReuse(context.Lane));
    }

    [Fact]
    public void CohortGateOptionsCarryEveryMember()
    {
        using var context = new CacheContext();
        AcceptanceCohortMemberBinding[] bindings = [Binding(Other, 'b'), Binding(Lead, 'a')];
        var identity = AcceptanceCohortIdentity.Create(bindings, new string('c', 40),
            new string('d', 40), "manifest-v1");
        var writer = new ConductEventLogWriter(Path.Combine(context.Root, ConductEventLogWriter.CurrentFileName));

        var options = ConductorDriver.CreateCohortGateExecutionOptions(writer, identity, bindings);

        Assert.Equal(new[] { new GoalId(Other), new GoalId(Lead) }, options.AttemptMemberGoalIds);
        Assert.True(options.CohortRemoteLanes);
        Assert.Equal(ConductorDriver.CohortGateRunIdentity(bindings.Select(binding => binding.GoalId.Value)),
            options.GateRunIdentity);
    }

    private static AcceptanceCohortMemberBinding Binding(string goalValue, char revision) => new(
        new GoalId(goalValue), new string(revision, 40), new string(revision, 40),
        ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FirstTests.cs"], ["ownership:tests"],
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    private sealed class CacheContext : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal List<string> Diagnostics { get; } = [];
        internal GoalAcceptanceVerifier.AcceptanceManifestCheck Lane { get; } = new()
        {
            Name = "infrastructure tests: alpha", Type = "dotnet-test", Runner = "vstest",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
        };
        internal AcceptancePartitionVerdictCacheOptions Options => new(
            null, Root, [Lane], 5, true,
            _ => "combined-tree", _ => "main-sha", _ => "combined-commit",
            () => "cohort-1", () => "manifest-a", () => false,
            ResolveClosureHash: _ => null, ResolveChangedFiles: (_, _, _) => [],
            ResolveDisjointLandingFacts: _ => null, MemberGoalIds: [Other, Lead], EmitDiagnostic: Diagnostics.Add);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
