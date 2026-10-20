using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

[Collection("IsolatedProcessSpawning")]
public sealed class DeferredNoChangeContextAuthorityTestsIsolatedProcessSpawning
{
    [Theory]
    [InlineData("missing-result")]
    [InlineData("malformed-result")]
    [InlineData("missing-metadata")]
    [InlineData("malformed-metadata")]
    [InlineData("wrong-goal")]
    [InlineData("wrong-candidate")]
    [InlineData("wrong-fingerprint")]
    [InlineData("conflicting-receipt-id")]
    [InlineData("wrong-selection")]
    [InlineData("wrong-member-selection")]
    [InlineData("serialized-result-escape")]
    [InlineData("result-candidate")]
    [InlineData("result-selection")]
    [InlineData("ambiguous-arm")]
    [InlineData("missing-trx")]
    [InlineData("changed-trx")]
    [InlineData("uncovered-trx")]
    [InlineData("escaped-result")]
    [InlineData("escaped-trx")]
    [InlineData("malformed-locator")]
    public void InvalidEvidenceNeverBecomesCurrentGreen(string control)
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var attempt = fixture.Attempt;
        var result = fixture.Result;
        var evidence = result.FocusedEvidence!;
        var arm = evidence.Arms![0];
        var path = arm.Checks[0].TestResultPaths![0];
        switch (control)
        {
            case "missing-result": File.Delete(attempt.ResultPath); break;
            case "malformed-result": File.WriteAllText(attempt.ResultPath, "{"); break;
            case "missing-metadata": File.Delete(attempt.MetadataPath); break;
            case "malformed-metadata": File.WriteAllText(attempt.MetadataPath, "{"); break;
            case "wrong-goal": fixture.WriteAttempt(attempt with { GoalId = new string('e', 32) }); break;
            case "wrong-candidate": fixture.WriteAttempt(attempt with { BranchHeadSha = new string('e', 40) }); break;
            case "wrong-fingerprint": fixture.WriteAttempt(attempt with { FindingRoundFingerprint = "other-round" }); break;
            case "conflicting-receipt-id": fixture.WriteAttempt(attempt with { FocusedEvidenceReceiptId = "other-receipt" }); break;
            case "wrong-selection": fixture.WriteAttempt(attempt with { FocusedEvidenceRequest = "infrastructure-tests:OtherTests" }); break;
            case "wrong-member-selection": fixture.WriteAttempt(attempt with { FocusedEvidenceMemberRequests = ["infrastructure-tests:OtherTests"] }); break;
            case "serialized-result-escape": fixture.WriteAttempt(attempt with { ResultPath = Path.Combine(fixture.Root, "outside.result.json") }); break;
            case "result-candidate": fixture.WriteResult(result with { BranchHeadSha = new string('e', 40) }); break;
            case "result-selection": fixture.WriteResult(result with { FocusedEvidence = evidence with { Request = "infrastructure-tests:OtherTests" } }); break;
            case "ambiguous-arm": fixture.WriteResult(result with { FocusedEvidence = evidence with { Arms = [arm, arm] } }); break;
            case "missing-trx": File.Delete(path); break;
            case "changed-trx": File.WriteAllText(path, "<TestRun />"); break;
            case "uncovered-trx": File.WriteAllText(path, File.ReadAllText(path).Replace("DeferredAlphaTests", "UnrelatedTests")); break;
            case "escaped-result":
                var escaped = Path.Combine(fixture.Root, "outside.result.json");
                File.Copy(attempt.ResultPath, escaped);
                fixture.Mark(fixture.Locator with { ResultPath = escaped });
                break;
            case "escaped-trx":
                var outsideTrx = Path.Combine(fixture.Root, "outside.trx");
                File.Copy(path, outsideTrx);
                fixture.WriteResult(result with { FocusedEvidence = evidence with
                { Arms = [arm with { Checks = [arm.Checks[0] with { TestResultPaths = [outsideTrx] }] }] } });
                break;
            case "malformed-locator":
                fixture.Kernel.RecordFindingEvidenceRun(fixture.Goal.Id, fixture.TesterA.Id,
                    "finding-evidence deferred-no-change outcome=green; task_id=duplicate; task_id=duplicate");
                break;
        }

        var context = fixture.Capture();
        var current = Assert.Single(context.Receipts, receipt => receipt.IsCurrent);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Unavailable, current.Availability);
        Assert.NotNull(current.Diagnostic);
        var summary = DeferredNoChangeContextFixture.Summary(fixture.Package(context: context));
        Assert.DoesNotContain(summary.GetProperty("Receipts").EnumerateArray(), receipt => receipt.GetProperty("CurrentGreenProof").GetBoolean());
        Assert.Empty(summary.GetProperty("CoveredSelections").EnumerateArray());
    }

    [Fact]
    public void OriginalShortNameCoverageAndMatchingNonNullReceiptIdentityRemainValid()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete(["DeferredAlphaTestsVariant"]);
        fixture.WriteAttempt(fixture.Attempt with { FocusedEvidenceReceiptId = fixture.Locator.ReceiptId });
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Green, Assert.Single(fixture.Capture().Receipts).Availability);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("unusable")]
    [InlineData("started")]
    public void AttemptOrderingAndPendingBarriersPreventOlderGreenFromMaskingNewerResult(string outcome)
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var old = fixture.Attempt;
        var oldMarker = fixture.Locator;
        var newerId = old.AttemptId + "-new";
        var newer = old with { AttemptId = newerId, Ordinal = old.Ordinal + 1,
            MetadataPath = Path.Combine(fixture.GoalStore, newerId + ".attempt.json"),
            ResultPath = Path.Combine(fixture.GoalStore, newerId + ".result.json"),
            Outcome = outcome == "red" ? ConductorParallelAcceptanceAttemptOutcome.Failed : ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts };
        fixture.WriteAttempt(newer);
        if (outcome == "red")
        {
            var source = fixture.Result;
            var evidence = source.FocusedEvidence!;
            var candidate = evidence.Arms![0];
            var trxPath = candidate.Checks[0].TestResultPaths![0];
            var redTrx = Path.Combine(fixture.GoalStore, "new-red.trx");
            File.WriteAllText(redTrx, File.ReadAllText(trxPath).Replace("outcome=\"Passed\"", "outcome=\"Failed\"")
                .Replace("passed=\"1\"", "passed=\"0\"").Replace("failed=\"0\"", "failed=\"1\""));
            var redCheck = candidate.Checks[0] with { Passed = false, TestResultPaths = [redTrx], FailingTestIdentities = ["DeferredAlphaTests.Runs"] };
            var red = source with { FocusedEvidence = evidence with { Passed = false, Checks = [redCheck], Arms =
                [candidate with { Passed = false, Disposition = FindingEvidenceArmDisposition.Red, Checks = [redCheck] }] } };
            File.WriteAllText(newer.ResultPath, JsonSerializer.Serialize(red, DeferredNoChangeContextFixture.JsonOptions));
        }
        fixture.Mark(oldMarker with { Outcome = outcome, ResultPath = outcome == "started" ? null : newer.ResultPath });
        // A duplicate old completion cannot change canonical attempt order or undo a pending marker.
        fixture.Mark(oldMarker);

        var context = fixture.Capture();
        var current = Assert.Single(context.Receipts, receipt => receipt.IsCurrent);
        Assert.Equal(outcome switch { "red" => DeferredNoChangeEvidenceAvailability.Red,
            "started" => DeferredNoChangeEvidenceAvailability.Pending, _ => DeferredNoChangeEvidenceAvailability.Unavailable }, current.Availability);
        Assert.Contains(context.Receipts, receipt => !receipt.IsCurrent && receipt.AttemptId == old.AttemptId && receipt.Availability == DeferredNoChangeEvidenceAvailability.Green);
        if (outcome != "started") Assert.Equal(newer.AttemptId, current.AttemptId);
        Assert.Empty(DeferredNoChangeContextFixture.Summary(fixture.Package(context: context)).GetProperty("CoveredSelections").EnumerateArray());
    }

    [Fact]
    public void IdenticalCandidateEvidenceRemainsAvailableWhenAnotherDeveloperRequestsIt()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var original = fixture.Developer;
        var requester = fixture.Kernel.AddTask(fixture.Goal.Id, AgentRole.Developer, "Verify unchanged revision",
            ConductorDriverTests.DefaultAgents());
        fixture.Kernel.RetryTask(fixture.Goal.Id, requester.Id, "Recheck unchanged candidate");
        ConductorDriverTests.DispatchTask(fixture.Kernel, fixture.Goal, requester,
            command: original.LastVerification!.Command, workingDirectory: fixture.Worktree, baseCommit: fixture.Candidate);
        fixture.Kernel.RecordDispatchExecutionResult(fixture.Goal.Id, requester.Id,
            original.LastVerification! with { CompletedAt = DateTimeOffset.UtcNow });
        Assert.Equal("deferred-no-change-round", requester.LastVerification!.CompletionVerdictRule);

        var context = fixture.Reader.CaptureDeferredEvidenceContext(fixture.Goal, fixture.Candidate);

        var current = Assert.Single(context.Receipts, receipt => receipt.IsCurrent);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Green, current.Availability);
        Assert.Equal(original.Id.Value, current.DeveloperTaskId);
    }

    [Fact]
    public void MissingLocatorAndUnusableBeforeRunAreVisibleWithoutDirectoryDiscovery()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        Assert.Equal("deferred-evidence-locator-unavailable", Assert.Single(fixture.Capture().Receipts).Diagnostic);
        fixture.Mark(new("unusable", fixture.Developer.Id, fixture.Candidate, "none", [], ["DeferredAlphaTests"], null, [], ["DeferredAlphaTests"]));
        var receipt = Assert.Single(fixture.Capture().Receipts);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Unavailable, receipt.Availability);
        Assert.Equal("deferred-evidence-unavailable-not-run", receipt.Diagnostic);
        Assert.Null(receipt.BodyJson);
        Assert.Null(receipt.CandidateArm);
    }

    [Fact]
    public void ChangeAttemptedAfterValidationCannotProduceGreenWithDifferentCapturedContent()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var resultPath = fixture.Locator.ResultPath!;
        var invoked = false;
        var captured = fixture.Reader.CaptureDeferredEvidenceContext(fixture.Goal, fixture.Candidate, () =>
        {
            invoked = true;
            File.WriteAllText(resultPath, "{\"changed\":true}");
        });
        Assert.True(invoked);
        var receipt = Assert.Single(captured.Receipts, receipt => receipt.IsCurrent);
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Unavailable, receipt.Availability);
        Assert.Null(receipt.BodyJson);
    }
}
