using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using MetadataFixture = GoalGitRefFingerprintReaderTests.MetadataFixture;

// Parallel-safe: each fixture owns its metadata, kernel, stores and injected git runner.
public sealed class TerminalGoalSweepAncestryReuseTests
{
    [Xunit.Fact]
    public void Run_UnchangedPendingGoal_ReusesAncestryAcrossThreeTicks()
    {
        using var fixture = new AncestryFixture();
        fixture.AddObligation();
        var cache = new TerminalGoalSweepCache();
        fixture.WarmIndex(cache);
        var before = fixture.Metadata.Commands.Count;
        var first = fixture.Run(cache);
        var firstCommands = fixture.Metadata.Commands.Skip(before).ToArray();
        var second = fixture.Run(cache);
        var secondCommands = fixture.Metadata.Commands.Skip(before + firstCommands.Length).ToArray();
        var third = fixture.Run(cache);
        var thirdCommands = fixture.Metadata.Commands.Skip(before + firstCommands.Length + secondCommands.Length).ToArray();

        Assert.Equal(1, fixture.AncestrySpawnCount);
        Assert.Equal(fixture.AncestryCommand(fixture.Metadata.TipSha),
            Assert.Single(firstCommands.Where(AncestryFixture.IsAncestryCommand)));
        Assert.Equal(first.GitSpawnCount - 1, second.GitSpawnCount);
        Assert.Equal(first.GitSpawnCount - 1, third.GitSpawnCount);
        Assert.Equal(firstCommands.Where(command => !AncestryFixture.IsAncestryCommand(command)).ToArray(), secondCommands);
        Assert.Equal(secondCommands, thirdCommands);
        Assert.DoesNotContain(fixture.Metadata.Commands.Skip(before), MetadataFixture.IndexCommands.Contains);
        foreach (var result in new[] { first, second, third })
        {
            Assert.Contains(fixture.Goal.Id, result.ExplicitlySweptGoalIds);
            Assert.Equal(0, result.CacheHitCount);
            AssertSameGoalResults(first, result, fixture.Metadata.Root, fixture.Metadata.Root);
        }
        fixture.AssertPending();
    }

    [Xunit.Fact]
    public void Run_NoPendingObligation_SkipsAncestryWithAndWithoutCache()
    {
        using var fixture = new AncestryFixture();
        var cache = new TerminalGoalSweepCache();
        var runs = new[] { fixture.Run(cache), fixture.Run(cache), fixture.Run(cache) };
        var uncached = fixture.Run(null);

        Assert.Equal(0, fixture.AncestrySpawnCount);
        Assert.Empty(fixture.Goal.OutstandingCriterionEvidenceObligations);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        foreach (var run in runs)
        {
            Assert.Contains(fixture.Goal.Id, run.ExplicitlySweptGoalIds);
            AssertSameGoalResults(uncached, run, fixture.Metadata.Root, fixture.Metadata.Root);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public void Run_PassedGateAfterWarmup_PreservesUncachedEvidenceOutcome(int exitCode)
    {
        using var cached = new AncestryFixture { ExitCode = exitCode };
        cached.AddObligation();
        using var uncached = new AncestryFixture(
            AgentOrchestratorKernel.FromSnapshot(cached.Kernel.ExportSnapshot())) { ExitCode = exitCode };
        var cache = new TerminalGoalSweepCache();
        cached.Run(cache);
        uncached.Run(null);
        cached.AssertPending();
        uncached.AssertPending();
        Assert.Equal(1, cached.AncestrySpawnCount);
        cached.WriteAttempt("passed", cached.Metadata.TipSha);
        uncached.WriteAttempt("passed", uncached.Metadata.TipSha);

        var actual = cached.Run(cache);
        var expected = uncached.Run(null);

        AssertSameGoalResults(expected, actual, uncached.Metadata.Root, cached.Metadata.Root);
        var cachedObligation = Assert.Single(cached.Goal.CriterionEvidenceObligations);
        var uncachedObligation = Assert.Single(uncached.Goal.CriterionEvidenceObligations);
        Assert.Equal(uncachedObligation.State, cachedObligation.State);
        Assert.Equal(uncachedObligation.CandidateSha, cachedObligation.CandidateSha);
        Assert.Equal(uncachedObligation.ReceiptId, cachedObligation.ReceiptId);
        Assert.Equal(1, cached.AncestrySpawnCount);
        Assert.Equal(exitCode == 0 ? 3 : 2, uncached.AncestrySpawnCount);
        if (exitCode == 0)
        {
            Assert.Equal(GoalStatus.Completed, cached.Goal.Status);
            Assert.Equal(GoalStatus.Completed, uncached.Goal.Status);
            Assert.Equal(CriterionEvidenceState.Satisfied, cachedObligation.State);
            Assert.Equal(cached.Metadata.TipSha, cachedObligation.CandidateSha);
            Assert.Contains(Assert.Single(actual.Goals).Repairs,
                repair => repair.Kind == "merge-evidence-terminalized" &&
                    repair.Evidence.Contains("acceptanceRecovery=", StringComparison.Ordinal));
        }
        else
        {
            cached.AssertPending();
            uncached.AssertPending();
            Assert.Empty(Assert.Single(actual.Goals).Repairs);
        }
    }

    private static void AssertSameGoalResults(
        TerminalGoalSweepResult expected, TerminalGoalSweepResult actual, string expectedRoot, string actualRoot)
    {
        var left = Assert.Single(expected.Goals);
        var right = Assert.Single(actual.Goals);
        Assert.Equal(left.GoalId, right.GoalId);
        Assert.Equal(left.GoalPrefix, right.GoalPrefix);
        Assert.Equal(left.Changed, right.Changed);
        // Independent roots contain the same attempt artifacts; compare all receipt fields after root substitution.
        Assert.Equal(JsonSerializer.Serialize(left.Reconciliation).Replace(expectedRoot.Replace("\\", "\\\\"), "$ROOT", StringComparison.Ordinal),
            JsonSerializer.Serialize(right.Reconciliation).Replace(actualRoot.Replace("\\", "\\\\"), "$ROOT", StringComparison.Ordinal));
        Assert.Equal(left.Blockers.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray(),
            right.Blockers.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray());
        Assert.Equal(left.Repairs.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray(),
            right.Repairs.Select(item => (item.Kind, item.Evidence, item.Command)).ToArray());
        Assert.Equal(expected.ExplicitlySweptGoalIds.ToArray(), actual.ExplicitlySweptGoalIds.ToArray());
        Assert.Equal(expected.ExplicitlyTerminalizedGoalIds.ToArray(), actual.ExplicitlyTerminalizedGoalIds.ToArray());
        Assert.Equal(expected.TerminalizedGoalCount, actual.TerminalizedGoalCount);
        Assert.Equal(expected.ResolvedAttentionItemCount, actual.ResolvedAttentionItemCount);
    }

    internal sealed class AncestryFixture : IDisposable
    {
        internal MetadataFixture Metadata { get; } = new();
        internal AgentOrchestratorKernel Kernel { get; }
        private readonly GoalId _goalId;
        internal Goal Goal => Kernel.GetGoal(_goalId);
        internal int ExitCode { get; set; } = 1;
        internal GitCli.GitResult? AncestryResult { get; set; }
        internal Dictionary<string, int> CandidateExitCodes { get; } = new(StringComparer.Ordinal);
        private readonly Func<string, IReadOnlyList<string>, GitCli.GitResult> _runner;
        internal int AncestrySpawnCount => Metadata.Commands.Count(IsAncestryCommand);

        internal AncestryFixture(AgentOrchestratorKernel? kernel = null)
        {
            Kernel = kernel ?? Metadata.Kernel;
            _goalId = new GoalId(Kernel.ExportSnapshot().Goals.Single().Id);
            Metadata.Tips.Clear();
            var branch = GoalWorktrees.BranchName(_goalId);
            Metadata.Tips[branch] = Metadata.TipSha;
            Metadata.Write($"refs/heads/{branch}", Metadata.TipSha + "\n");
            _runner = RunGit;
        }

        internal void AddObligation()
        {
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec(
                Goal.Objective, ["Full gate passes"], VerificationClass.TestVerifiable, [], []));
            BindCandidate(Metadata.TipSha);
            AssertPending();
        }

        internal void BindCandidate(string candidate) =>
            Kernel.MapCriterionEvidenceOwner(Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);

        internal void AssertPending()
        {
            Assert.Equal(GoalStatus.Verified, Goal.Status);
            var obligation = Assert.Single(Goal.CriterionEvidenceObligations);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Null(obligation.CandidateSha);
        }

        internal void WarmIndex(TerminalGoalSweepCache cache)
        {
            GoalGitFactIndex.Build(Metadata.Root, "main", _runner, cache.BranchContentStates, cache.GitFactSnapshots);
            Assert.Equal(MetadataFixture.IndexCommands, Metadata.Commands.ToArray());
        }

        internal TerminalGoalSweepResult Run(TerminalGoalSweepCache? cache)
        {
            var before = Metadata.Commands.Count;
            var result = TerminalGoalSweep.Run(Kernel, Metadata.Root, "main", cache: cache,
                integrationEvidenceResolver: new NoIntegrationEvidence(), gitRunner: _runner);
            Assert.Equal(Metadata.Commands.Count - before, result.GitSpawnCount);
            Assert.Contains(Goal.Id, result.ExplicitlySweptGoalIds);
            return result;
        }

        internal string AncestryCommand(string candidate) =>
            $"merge-base --is-ancestor {candidate} {Metadata.MainSha}";

        internal static bool IsAncestryCommand(string command) =>
            command.StartsWith("merge-base --is-ancestor ", StringComparison.Ordinal);

        private GitCli.GitResult RunGit(string directory, IReadOnlyList<string> args)
        {
            if (args.Count < 2 || args[0] != "merge-base" || args[1] != "--is-ancestor")
                return Metadata.RunGit(directory, args);

            Assert.Equal(Metadata.Root, directory);
            Assert.Equal(4, args.Count);
            Metadata.Commands.Add(string.Join(" ", args));
            return AncestryResult ?? new(CandidateExitCodes.GetValueOrDefault(args[2], ExitCode), string.Empty, string.Empty);
        }

        internal void WriteAttempt(string attemptId, string candidate)
        {
            var directory = Path.Combine(Metadata.Root, ".orchestrator", "acceptance-gate-attempts", Goal.Id.Value);
            Directory.CreateDirectory(directory);
            var prefix = Path.Combine(directory, attemptId);
            File.WriteAllText(prefix + ".attempt.json", JsonSerializer.Serialize(new
            {
                attemptId,
                goalId = Goal.Id.Value,
                branchHeadSha = candidate,
                kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
                focusedEvidenceRequest = (string?)null,
                startedAt = "2026-10-11T00:00:00Z",
                executionProtocol = "out-of-process"
            }));
            File.WriteAllText(prefix + ".result.json", JsonSerializer.Serialize(new
            {
                kind = "accepted",
                acceptance = new { passed = true }
            }));
            File.WriteAllText(prefix + ".exit.txt", "0");
        }

        public void Dispose() => Metadata.Dispose();
    }

    private sealed class NoIntegrationEvidence : IGoalIntegrationEvidenceResolver
    {
        public bool TryResolve(GoalId goalId, out GoalIntegrationEvidence? evidence)
        {
            evidence = null;
            return false;
        }
    }
}
