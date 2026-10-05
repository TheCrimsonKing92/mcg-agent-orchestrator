using Mcg.AgentOrchestrator.Core;

public sealed class FailureClusterReportTests
{
    [Fact]
    public void RealFixturesRankGreenReceiptsAndChargeDiscardedCohortsOnce()
    {
        var rows = FailureClusterFixtureData.Read().Build(FailureClusterFixtureData.Since, FailureClusterFixtureData.Until);
        var green = rows.Where(r => r.MessageFamily.Contains("receipt was attached")).ToArray();
        Assert.NotEmpty(green);
        Assert.Contains(rows.Take(3), r => r.MessageFamily.Contains("receipt was attached"));
        Assert.Equal(14, green.Sum(r => r.PaidRounds));
        var dirty = Assert.Single(rows, r => r.MessageFamily.Contains("dirty worktree"));
        Assert.Equal(3, dirty.GoalsAffected.Count);
        Assert.True(dirty.TotalCost > 0);
        var failed = Assert.Single(rows, r => r.EventKind == "TaskFailed" && r.Marker == "rule=unknown-failure");
        Assert.Equal("claude", failed.WorkerCli);
        Assert.Equal(4, failed.GoalsAffected.Count);
        Assert.True(failed.TotalCost > 0);
        var permit = Assert.Single(rows, r => r.Marker == "reason=structural-coverage-permit-unavailable");
        Assert.Equal(2, permit.RootEvents);
        Assert.Equal((1650010d + 1496460d) / 60000, permit.GateMinutes, 8);
        Assert.Equal(0, permit.PaidRounds + permit.KnockOnRounds + permit.OperatorTouches);
        Assert.Equal(permit.GateMinutes * FailureClusterReport.GateMinuteWeight, permit.TotalCost);
        Assert.True(permit.TotalCost > 0);
        Assert.Single(rows, r => r.EventKind == "GateAttempt");
        Assert.DoesNotContain(rows, r => r.MessageFamily.Contains("held at"));
    }

    [Fact]
    public void SyntheticDecisionTableRanksCostsAndKeepsRuleAndCli()
    {
        var rows = FailureClusterTestData.Create().Build(FailureClusterTestData.Since, FailureClusterTestData.Until);
        var green = Assert.Single(rows, r => r.MessageFamily.Contains("receipt was attached"));
        Assert.True(Array.IndexOf(rows.ToArray(), green) < 3);
        Assert.Equal(3, green.PaidRounds);
        Assert.Equal(ReworkCauseFamily.EvidenceRerun, green.Family);
        Assert.True(Assert.Single(rows, r => r.MessageFamily.Contains("dirty worktree")).TotalCost > 0);
        var failed = Assert.Single(rows, r => r.EventKind == "TaskFailed");
        Assert.Equal("rule=unknown-failure", failed.Marker);
        Assert.Equal("claude", failed.WorkerCli);
        Assert.True(failed.TotalCost > 0);
        var permit = Assert.Single(rows, r => r.Marker == "reason=structural-coverage-permit-unavailable");
        Assert.Equal(5, permit.GateMinutes);
        Assert.Equal(permit.GateMinutes * FailureClusterReport.GateMinuteWeight, permit.TotalCost);
        Assert.Equal(0, permit.PaidRounds + permit.KnockOnRounds + permit.OperatorTouches);
        Assert.Equal(3, FailureClusterTestData.Create().Build(FailureClusterTestData.Since,
            FailureClusterTestData.Until).Count(r => r.TotalCost == 3));
    }

    public static IEnumerable<object[]> Holds() => FailureClusterReport.SteadyStateHoldPrefixes.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Holds))]
    public void EverySteadyHoldPrefixIsExcludedButEscalationRemains(string prefix)
    {
        var at = FailureClusterTestData.Since;
        var rows = FailureClusterReport.Build([
            new("GoalLifecycleDecision", "Batch loop tick 9: held at WorkspaceReady — " + prefix + " extra", "goal", null, at),
            new("GoalEscalated", "escalated at WorkspaceReady — dirty worktree", "goal", null, at)
        ], [], [], at, at.AddDays(1));
        Assert.Equal("escalated at WorkspaceReady — dirty worktree", Assert.Single(rows).MessageFamily);
    }

    [Fact]
    public void OperatorTouchesStopAtTheNextGoalDispatchAndWindowIsHalfOpen()
    {
        var at = FailureClusterTestData.Since;
        var rows = FailureClusterReport.Build([
            new("TaskFailed", "Dispatch failed: exit code 1: codex exec", "goal", "task", at),
            new("TaskDispatched", "next worker", "goal", "other-task", at.AddHours(2)),
            new("TaskRetried", "outside", "goal", "task", at.AddDays(1))
        ], [], [new("goal", null, at.AddMinutes(-1)), new("goal", null, at),
            new("goal", null, at.AddHours(1)), new("goal", null, at.AddHours(2))], at, at.AddDays(1));
        var row = Assert.Single(rows);
        Assert.Equal(2, row.OperatorTouches);
        Assert.Equal(2 * FailureClusterReport.OperatorTouchWeight, row.TotalCost);
        Assert.Contains("key=" + row.Key, row.Query);
        Assert.Contains("--since " + at.ToString("O"), row.Query);
    }

    [Theory]
    [InlineData("passed", 0)]
    [InlineData("completed", 0)]
    [InlineData("unknown", 0)]
    [InlineData("failed", 1)]
    [InlineData("faulted", 1)]
    [InlineData("cancelled", 1)]
    [InlineData("InfrastructureFailure", 1)]
    [InlineData("", 0)]
    public void GateMinutesRequirePositiveNonPassedOutcome(string outcome, int expected)
    {
        var at = FailureClusterTestData.Since;
        var rows = FailureClusterReport.Build([], [
            new(at, "gate-progress", "goal", $"PHASE_PROGRESS phase=gate-phase-breakdown elapsed_ms=60000 target=scope=verifier-run;outcome={outcome}")
        ], [], at, at.AddDays(1));
        Assert.Equal(expected, rows.Count);
        if (expected > 0) Assert.Equal(1, Assert.Single(rows).GateMinutes);
    }

    [Fact]
    public void NormalizeStripsIdentityNumbersPathsAndDurationsWithoutMergingDifferentRules()
    {
        var a = FailureClusterReport.Normalize("task=aaaaaaaa sha=1234567 C:\\repo\\a.cs 12ms 2026-09-21T12:00:00Z rule=red");
        var b = FailureClusterReport.Normalize("task=bbbbbbbb sha=7654321 D:\\other\\b.cs 24ms 2026-10-01T13:00:00Z rule=red");
        Assert.Equal(a, b);
        Assert.NotEqual(a, b.Replace("rule=red", "rule=green"));
    }

    [Fact]
    public void TaskFailed_DifferentSessionGuidsAcrossDispatches_ClusterHasTwoRoots()
    {
        var at = FailureClusterTestData.Since;
        const string command = "Dispatch failed: rule=unknown-failure; exit code 1: claude -p --session-id ";
        var rows = FailureClusterReport.Build([
            new("TaskFailed", command + "f62cf60c-60c5-4807-825e-8204e3e0daa4", "goal", "task", at),
            new("TaskDispatched", "next worker", "goal", "task", at.AddMinutes(1)),
            new("TaskFailed", command + "01ABCDEF-1234-5678-9ABC-DEF012345678", "goal", "task", at.AddMinutes(2))
        ], [], [], at, at.AddDays(1));

        var row = Assert.Single(rows);
        Assert.Equal(2, row.RootEvents);
        Assert.Equal("TaskFailed", row.EventKind);
        Assert.Equal("rule=unknown-failure", row.Marker);
        Assert.Equal("claude", row.WorkerCli);
        Assert.EndsWith("--session-id <id>", row.MessageFamily);
    }

    [Fact]
    public void CohortMemberProgressChargesOneAttemptAndKeepsEveryAffectedGoal()
    {
        var at = FailureClusterTestData.Since;
        var rows = FailureClusterReport.Build([], [
            new(at, "gate-progress", "11111111", "PHASE_PROGRESS goal=11111111 cohort=same-candidates members=11111111,22222222 phase=gate-phase-breakdown elapsed_ms=60000 target=scope=verifier-run;outcome=InfrastructureFailure"),
            new(at.AddMilliseconds(2), "gate-progress", "22222222", "PHASE_PROGRESS goal=22222222 cohort=same-candidates members=11111111,22222222 phase=gate-phase-breakdown elapsed_ms=60000 target=scope=verifier-run;outcome=InfrastructureFailure"),
            new(at.AddSeconds(1), "acceptance-cohort", "11111111", "ACCEPTANCE_COHORT_EXIT goal=11111111 members=11111111,22222222 outcome=InfrastructureFailure reason=structural-coverage-permit-unavailable"),
            new(at.AddHours(1), "gate-progress", "11111111", "PHASE_PROGRESS goal=11111111 cohort=same-candidates members=11111111,22222222 phase=gate-phase-breakdown elapsed_ms=120000 target=scope=verifier-run;outcome=InfrastructureFailure"),
            new(at.AddHours(1).AddSeconds(1), "acceptance-cohort", "11111111", "ACCEPTANCE_COHORT_EXIT goal=11111111 members=11111111,22222222 outcome=InfrastructureFailure reason=structural-coverage-permit-unavailable")
        ], [], at, at.AddDays(1));
        var row = Assert.Single(rows);
        Assert.Equal(2, row.RootEvents);
        Assert.Equal(3, row.GateMinutes);
        Assert.Equal(new[] { "11111111", "22222222" }, row.GoalsAffected);
        Assert.Equal(3 * FailureClusterReport.GateMinuteWeight, row.TotalCost);
    }

    [Fact]
    public void InvalidationsUseHalfWeightAndCliFallbackSeparatesProviders()
    {
        var at = FailureClusterTestData.Since;
        var rows = FailureClusterReport.Build([
            new("TaskRetried", "Invalidated Reviewer because Developer changed", "goal", "reviewer", at),
            new("TaskFailed", "Dispatch failed: exit code 1: codex exec", "goal", "developer", at),
            new("TaskFailed", "Dispatch failed: exit code 1: claude -p", "goal", "developer", at)
        ], [], [], at, at.AddDays(1));
        var invalidated = Assert.Single(rows, r => r.KnockOnRounds > 0);
        Assert.Equal(0, invalidated.PaidRounds);
        Assert.Equal(1, invalidated.KnockOnRounds);
        Assert.Equal(ReworkCauseFamily.DownstreamRerun, invalidated.Family);
        Assert.Equal(0.5, invalidated.TotalCost);
        Assert.Equal(new[] { "claude", "codex" }, rows.Where(r => r.EventKind == "TaskFailed").Select(r => r.Marker).Order());
    }
}
