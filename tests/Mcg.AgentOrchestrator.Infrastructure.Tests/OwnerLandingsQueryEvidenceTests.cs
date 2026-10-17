using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = OwnerLandingsQueryTests.Fixture;

// Parallel-safe: attempt files live only under the current case's private root.
public sealed class OwnerLandingsQueryEvidenceTests
{
    [Fact]
    public void Load_MatchingPasses_ChoosesNewestCandidateIdentity()
    {
        using var fixture = new Fixture();
        var goal = fixture.Add("10000001", fixture.Clock.Now);
        fixture.WriteAttempt(goal, "older111-attempt", fixture.Clock.Now.AddHours(-2));
        fixture.WriteAttempt(goal, "newest22-attempt", fixture.Clock.Now.AddHours(-1), Fixture.Candidate.ToUpperInvariant(),
            "acceptance passed with receipt");
        // A closer pass for a different candidate must never replace the matching receipt.
        fixture.WriteAttempt(goal, "wrong333-attempt", fixture.Clock.Now, new string('e', 40));
        var malformed = fixture.WriteAttempt(goal, "broken44-attempt", fixture.Clock.Now);
        File.WriteAllText(malformed, "{not json");

        var result = fixture.Load();

        Assert.Equal("acceptance newest22 passed 2026-10-16 11:00 local", Assert.Single(result.Rows).Acceptance);
        Assert.Contains("  acceptance newest22 passed 2026-10-16 11:00 local", OwnerLandingsQuery.Render(result));
    }

    [Theory]
    [InlineData("missing-folder")]
    [InlineData("wrong-candidate")]
    [InlineData("failed")]
    [InlineData("wrong-kind")]
    [InlineData("bad-json")]
    [InlineData("non-object")]
    [InlineData("bad-time")]
    [InlineData("wrong-value-kind")]
    [InlineData("unreadable-folder")]
    public void Load_UnusableAttempt_KeepsLandingAndDisclosesMissingEvidence(string scenario)
    {
        using var fixture = new Fixture();
        var goal = fixture.Add("10000001", fixture.Clock.Now);
        if (scenario == "unreadable-folder")
        {
            var parent = Path.Combine(fixture.DirectoryPath, "acceptance-gate-attempts");
            Directory.CreateDirectory(parent);
            File.WriteAllText(Path.Combine(parent, goal.Id), "a file cannot be enumerated as a directory");
        }
        else if (scenario != "missing-folder")
        {
            var path = fixture.WriteAttempt(goal, "attempt1-test", fixture.Clock.Now,
                scenario == "wrong-candidate" ? new string('e', 40) : Fixture.Candidate,
                scenario == "failed" ? "acceptance failed" : "acceptance passed",
                scenario == "wrong-kind" ? "build" : "gate");
            if (scenario == "bad-json") File.WriteAllText(path, "{not json");
            if (scenario == "non-object") File.WriteAllText(path, "[]");
            if (scenario == "bad-time") File.WriteAllText(path,
                """{"kind":"gate","detail":"acceptance passed","branchHeadSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","completedAt":"invalid"}""");
            if (scenario == "wrong-value-kind") File.WriteAllText(path, """{"kind":4,"detail":{}}""");
        }

        var result = fixture.Load();

        var row = Assert.Single(result.Rows);
        Assert.Equal(goal.Id, row.GoalId);
        Assert.Equal("acceptance evidence unavailable", row.Acceptance);
        Assert.DoesNotContain("passed", string.Join("\n", OwnerLandingsQuery.Render(result)));
    }

    [Fact]
    public void Load_NoFacts_CountsOnlyCompletedGoalsAcrossAllWindows()
    {
        using var fixture = new Fixture();
        var dated = fixture.Add("10000001", fixture.Clock.Now, status: nameof(GoalStatus.Verified));
        fixture.Goals.Add(new GoalSummary("20000002", nameof(GoalStatus.Completed), "Older goal", "1999-01-01"));
        fixture.Goals.Add(new GoalSummary("30000003", nameof(GoalStatus.Completed), "Recent goal", fixture.Clock.Now.ToString("O")));

        var before = fixture.Load("today");
        fixture.Goals.Add(new GoalSummary("40000004", nameof(GoalStatus.Verified), "Worker finished", fixture.Clock.Now.ToString("O")));
        var after = fixture.Load("today");

        Assert.Equal(dated.Id, Assert.Single(after.Rows).GoalId);
        Assert.Equal(2, before.UndatedCount);
        Assert.Equal(2, after.UndatedCount);
        Assert.Equal(2, fixture.Load("30").UndatedCount);
        var rendered = OwnerLandingsQuery.Render(after);
        Assert.Equal("Undated: 2 landed goals have no recorded landing time", rendered[^1]);
        foreach (var prefix in new[] { "20000002", "30000003", "40000004" })
            Assert.DoesNotContain(rendered, line => line.Contains(prefix, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Text_StateReadFailure_DisclosesUnavailable(bool denied)
    {
        using var fixture = new Fixture();
        fixture.ReadError = denied ? new UnauthorizedAccessException("denied") : new IOException("unreadable");

        Assert.Equal("landings unavailable: " + fixture.ReadError.Message,
            OwnerLandingsQuery.Text(fixture, fixture.DirectoryPath, fixture.Clock, ""));
        Assert.Equal(1, fixture.Reads);
    }
}
