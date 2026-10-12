using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProspectiveEvidenceGateResolverNegativeTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void BlankSelectorCannotResolveEvenWithMatchingPassedRow(string? blank)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass, blank!]);
        fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows.Append(($"{blank}.Check", "Passed")));

        var result = fixture.Resolve();

        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(result.ResolvedRequestIds);
        Assert.Equal(wait.Id, Assert.Single(result.UnresolvedWaits).RequestId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_WriterFails_WaitStaysPendingAndRetryCanComplete(bool blockEventFile)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass]);
        var (attemptId, trx, _) = fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows);
        var eventsDirectory = Path.Combine(fixture.Root, "blocked-events");
        var writer = new GoalLifecycleEventWriter(eventsDirectory);
        var blockedPath = blockEventFile ? writer.EventFilePath(fixture.Goal.Id) : eventsDirectory;
        if (blockEventFile)
            Directory.CreateDirectory(blockedPath);
        else
            File.WriteAllText(blockedPath, "A file cannot be an events directory.");
        fixture.Kernel.SetEventWriter(writer);

        var result = fixture.Resolve();

        AssertPending(wait, result, []);
        var failure = Assert.Single(result.UnresolvedWaits).SubmissionFailure;
        Assert.Contains(blockEventFile ? nameof(UnauthorizedAccessException) : nameof(IOException), failure, StringComparison.Ordinal);
        Assert.Contains(blockedPath, failure, StringComparison.Ordinal);
        Assert.Empty(wait.AnswerHistory);
        Assert.Equal(wait.Id, Assert.Single(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id)).Id);
        Assert.DoesNotContain(fixture.Goal.Timeline, entry => entry.Kind == ProgressKind.HumanInputReceived);
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        var restoredWait = Assert.Single(restored.GetPendingHumanInput(fixture.Goal.Id));
        Assert.False(restoredWait.IsCompleted);
        Assert.Null(restoredWait.Answer);

        if (blockEventFile)
            Directory.Delete(blockedPath);
        else
            File.Delete(blockedPath);
        var retry = fixture.Resolve();

        Assert.Equal([wait.Id], retry.ResolvedRequestIds);
        Assert.Empty(retry.UnresolvedWaits);
        Assert.True(wait.IsCompleted);
        Assert.Contains(attemptId, wait.Answer, StringComparison.Ordinal);
        Assert.Contains(trx, wait.Answer, StringComparison.Ordinal);
        Assert.Single(wait.AnswerHistory);
        Assert.Single(fixture.Goal.Timeline, entry => entry.Kind == ProgressKind.HumanInputReceived);
        Assert.True(Directory.Exists(eventsDirectory));
        Assert.True(File.Exists(writer.EventFilePath(fixture.Goal.Id)));
        var persisted = JsonNode.Parse(File.ReadAllText(writer.EventFilePath(fixture.Goal.Id)))!;
        Assert.Equal(wait.Answer, persisted["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("missing-row")]
    [InlineData("failed-row")]
    [InlineData("no-class-results")]
    [InlineData("wrong-candidate")]
    [InlineData("failed-attempt")]
    public void IncompleteOrIneligibleEvidenceLeavesWaitPendingWithExactNames(string scenario)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var names = scenario == "no-class-results"
            ? [ProspectiveEvidenceGateFixture.IncidentClass] : ProspectiveEvidenceGateFixture.IncidentRows;
        var wait = fixture.CreateWait(names);
        var rows = ProspectiveEvidenceGateFixture.PassingRows.ToArray();
        if (scenario == "missing-row") rows = rows[..^1];
        if (scenario == "failed-row") rows[^1] = (rows[^1].Name, "Failed");
        if (scenario == "no-class-results") rows = [("OtherTests.Check", "Passed")];
        fixture.WriteAttempt(rows,
            candidateSha: scenario == "wrong-candidate" ? new string('b', 40) : null,
            outcome: scenario == "failed-attempt" ? ConductorParallelAcceptanceAttemptOutcome.Failed : ConductorParallelAcceptanceAttemptOutcome.Passed);

        var result = fixture.Resolve();

        var expected = scenario is "missing-row" or "failed-row" ? [names[^1]] : names;
        AssertPending(wait, result, expected);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("NotExecuted")]
    [InlineData("Inconclusive")]
    [InlineData("Timeout")]
    public void EveryMatchingResultMustPassEvenWhenAnotherMatchingRowPassed(string outcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass]);
        fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows
            .Append((ProspectiveEvidenceGateFixture.IncidentRows[0], outcome)));

        AssertPending(wait, fixture.Resolve(), [ProspectiveEvidenceGateFixture.IncidentClass]);
    }

    [Theory]
    [InlineData("GateTestsExtra.Check")]
    [InlineData("GateTests[case]")]
    [InlineData("gatetests.Check")]
    public void SelectorRequiresOrdinalEqualityOrDotOrParenthesisBoundary(string resultName)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(["GateTests"]);
        fixture.WriteAttempt([(resultName, "Passed")]);

        AssertPending(wait, fixture.Resolve(), ["GateTests"]);
    }

    [Theory]
    [InlineData("missing-trx")]
    [InlineData("malformed-trx")]
    [InlineData("missing-metadata")]
    [InlineData("malformed-metadata")]
    [InlineData("missing-outcome")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-goal")]
    public void UnreadableOrUnprovenAttemptFailsClosed(string scenario)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass]);
        var (_, trx, metadata) = fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows);
        switch (scenario)
        {
            case "missing-trx": File.Delete(trx); break;
            case "malformed-trx": File.WriteAllText(trx, "<TestRun>"); break;
            case "missing-metadata": File.Delete(metadata); break;
            case "malformed-metadata": File.WriteAllText(metadata, "{"); break;
            default:
                var json = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
                if (scenario == "missing-outcome") json.Remove("outcome");
                if (scenario == "wrong-kind") json["kind"] = "pre-review-evidence";
                if (scenario == "wrong-goal") json["goalId"] = GoalId.New().Value;
                File.WriteAllText(metadata, json.ToJsonString());
                break;
        }

        AssertPending(wait, fixture.Resolve(), [ProspectiveEvidenceGateFixture.IncidentClass]);
    }

    [Fact]
    public void MissingAdditionalTrxRejectsEntireAttempt()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([ProspectiveEvidenceGateFixture.IncidentClass]);
        fixture.WriteAttempt(ProspectiveEvidenceGateFixture.PassingRows,
            additionalPaths: [Path.Combine(fixture.Root, "missing.trx")]);

        AssertPending(wait, fixture.Resolve(), [ProspectiveEvidenceGateFixture.IncidentClass]);
    }

    [Fact]
    public void SeparateIncompleteAttemptsCannotBeStitchedAndNewestMissingNamesWin()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(["FirstTests", "SecondTests", "SecondTests"]);
        fixture.WriteAttempt([("SecondTests.Check", "Passed")], completedAt: DateTimeOffset.Parse("2026-10-12T00:00:00Z"));
        fixture.WriteAttempt([("FirstTests.Check", "Passed")], completedAt: DateTimeOffset.Parse("2026-10-12T01:00:00Z"));

        AssertPending(wait, fixture.Resolve(), ["SecondTests"]);
    }

    [Fact]
    public void OlderCompletePassingAttemptCanSatisfyWithoutPoolingRuns()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(["FirstTests", "SecondTests"]);
        var (olderId, olderTrx, _) = fixture.WriteAttempt([("FirstTests.Check", "Passed"), ("SecondTests.Check", "Passed")],
            completedAt: DateTimeOffset.Parse("2026-10-12T00:00:00Z"));
        var (newerId, _, _) = fixture.WriteAttempt([("FirstTests.Check", "Passed")],
            completedAt: DateTimeOffset.Parse("2026-10-12T01:00:00Z"));

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Contains(olderId, wait.Answer, StringComparison.Ordinal);
        Assert.Contains(olderTrx, wait.Answer, StringComparison.Ordinal);
        Assert.DoesNotContain(newerId, wait.Answer, StringComparison.Ordinal);
    }

    private static void AssertPending(HumanInputRequest wait, ProspectiveEvidenceGateResolution result, string[] expected)
    {
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(result.ResolvedRequestIds);
        var unresolved = Assert.Single(result.UnresolvedWaits);
        Assert.Equal(wait.Id, unresolved.RequestId);
        Assert.Equal(expected, unresolved.MissingOrFailedTestNames);
    }
}
