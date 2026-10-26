using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static PreTesterGreenSubclassCoverageTests;

// Parallel-safe: fixture-owned TRX files and pure marker formatting, with no shared state.
public sealed class PreTesterUnusableCheckTests
{
    [Theory]
    [InlineData("not-completed")]
    [InlineData("evidence-invalid")]
    [InlineData("candidate-arm-missing")]
    [InlineData("candidate-arm-not-accepted")]
    [InlineData("candidate-arm-not-passed")]
    [InlineData("candidate-arm-not-green")]
    [InlineData("executed-count-missing")]
    public void ArmLevelFailuresNameTheFirstFailedCondition(string expectedCheck)
    {
        using var fixture = new TrxFixture("A");
        var evidence = fixture.Evidence;
        var arm = Assert.Single(evidence.Arms!);
        switch (expectedCheck)
        {
            case "evidence-invalid": evidence = evidence with { Passed = false }; break;
            case "candidate-arm-missing": arm = arm with { Sha = "other-sha" }; break;
            case "candidate-arm-not-accepted": arm = arm with { Accepted = false }; break;
            case "candidate-arm-not-passed": arm = arm with { Passed = false }; break;
            case "candidate-arm-not-green": arm = arm with { Disposition = FindingEvidenceArmDisposition.Inconclusive }; break;
            case "executed-count-missing":
                arm = arm with { Checks = arm.Checks.Select((check, index) => index == 0
                    ? check with { ExecutedTestCount = null } : check).ToArray() };
                break;
        }
        evidence = evidence with { Arms = [arm] };

        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence,
            completed: expectedCheck != "not-completed", TrxFixture.CandidateSha, fixture.Selections, _ => false);

        Assert.Equal("unusable", result.Outcome);
        var reason = Assert.IsType<PreTesterUnusableReason>(result.Unusable);
        Assert.Equal(expectedCheck, reason.Check);
        Assert.Empty(reason.MissingClasses);
    }

    [Fact]
    public void AppendToAddsTheCheckOnlyForUnusableAndParseIgnoresIt()
    {
        var entry = new PreTesterEvidenceEntry("green", TrxFixture.CandidateSha, "receipt-1",
            ["Infrastructure.Tests:ConductorBatchLoopTests"], ["Infrastructure.Tests:OtherTests"],
            "result.json", ["ExampleTests.Fails"]);
        var green = PreTesterEvidenceIndexLines.FormatMarker(entry);
        Assert.Equal(green, PreTesterUnusableReason.AppendTo(null, green));
        var line = PreTesterEvidenceIndexLines.FormatMarker(entry with { Outcome = "unusable" });
        var missing = PreTesterUnusableReason.AppendTo(new("required-class-missing", ["ConductorBatchLoopTests"]), line);
        Assert.Equal(line + "; unusable_check=required-class-missing; unusable_classes=ConductorBatchLoopTests", missing);
        var invalid = PreTesterUnusableReason.AppendTo(new("evidence-invalid", []), line);
        Assert.EndsWith("; unusable_check=evidence-invalid", invalid, StringComparison.Ordinal);
        Assert.DoesNotContain("unusable_classes=", invalid, StringComparison.Ordinal);

        var before = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(line));
        foreach (var appended in new[] { missing, invalid })
        {
            var after = Assert.IsType<PreTesterEvidenceEntry>(PreTesterEvidenceIndexLines.Parse(appended));
            Assert.Equal(before.Outcome, after.Outcome);
            Assert.Equal(before.CandidateSha, after.CandidateSha);
            Assert.Equal(before.ReceiptId, after.ReceiptId);
            Assert.Equal(before.Selections, after.Selections);
            Assert.Equal(before.NotRun, after.NotRun);
            Assert.Equal(before.ResultPath, after.ResultPath);
            Assert.Equal(before.FailingTests, after.FailingTests);
        }

        Assert.Equal(line + "; unusable_check=required-class-missing; unusable_classes=Space%20Class,Comma%2CClass",
            PreTesterUnusableReason.AppendTo(new("required-class-missing", ["Space Class", "Comma,Class"]), line));
    }
}
