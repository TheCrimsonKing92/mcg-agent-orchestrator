using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorDriverTestsAttributionPending.Fixture;

public sealed class AcceptanceLaneRerunAttributionTests
{
    [Fact]
    public void DuplicateCheckNamesCannotSupplyConfirmation()
    {
        using var fixture = new Fixture();
        var summary = new AcceptanceVerificationSummary(false, [fixture.Check, fixture.Check], BranchHeadSha: Fixture.Candidate,
            MainHeadSha: Fixture.Main, AttemptId: Fixture.Attempt);
        var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, summary, new(() => ["src/Changed.cs"]))!;
        Assert.All(reading.FailingTests, test => Assert.Null(test.ConfirmedLaneFailure));
    }

    [Theory]
    [InlineData("missing-proof")]
    [InlineData("foreign-goal")]
    [InlineData("stale-candidate")]
    [InlineData("wrong-main")]
    [InlineData("wrong-attempt")]
    [InlineData("wrong-check")]
    [InlineData("wrong-invocation")]
    [InlineData("wrong-ordinal")]
    [InlineData("cross-attempt-reuse")]
    [InlineData("missing-receipt")]
    [InlineData("changed-bytes")]
    [InlineData("malformed")]
    [InlineData("zero-tests")]
    [InlineData("zero-executed-count")]
    [InlineData("foreign-current-receipt")]
    [InlineData("not-executed")]
    [InlineData("wrong-test")]
    [InlineData("duplicate-test")]
    [InlineData("duplicate-run")]
    [InlineData("nonexecuted-predicate")]
    [InlineData("passing-rerun")]
    public void InvalidEvidenceCannotConfirmCandidateExecutionThroughMaterializer(string mutation)
    {
        using var fixture = new Fixture();
        var check = fixture.Check;
        var lane = check.LaneRerun!;
        var proof = lane.Attribution!;
        var path = lane.RerunTestResultPaths.Single();
        switch (mutation)
        {
            case "missing-proof": lane = lane with { Attribution = null }; break;
            case "foreign-goal": lane = lane with { Attribution = proof with { GoalId = "other-goal" } }; break;
            case "stale-candidate": lane = lane with { Attribution = proof with { CandidateSha = "stale-candidate" } }; break;
            case "wrong-main": lane = lane with { Attribution = proof with { MainSha = "foreign-main" } }; break;
            case "wrong-attempt": lane = lane with { Attribution = proof with { AttemptId = "earlier-attempt" } }; break;
            case "wrong-check": lane = lane with { Attribution = proof with { CheckName = "other-check" } }; break;
            case "wrong-invocation": lane = lane with { RerunInvocationId = "foreign-attempt:check:1" }; break;
            case "wrong-ordinal": check = check with { TestResultRunOrdinal = 2 }; break;
            case "cross-attempt-reuse": check = check with { TestResultIsExplicitCrossAttemptReuse = true }; break;
            case "missing-receipt": File.Delete(path); break;
            case "changed-bytes": File.AppendAllText(path, "\n"); break;
            case "malformed": File.WriteAllText(path, "<not-valid"); break;
            case "zero-tests": File.WriteAllText(path, "<TestRun id='empty'><Results /></TestRun>"); break;
            case "zero-executed-count": check = check with { ExecutedTestCount = 0 }; break;
            case "foreign-current-receipt": check = check with { TestResultPaths = lane.FirstTestResultPaths }; break;
            case "not-executed": File.WriteAllText(path, File.ReadAllText(path).Replace("outcome=\"Failed\"", "outcome=\"NotExecuted\"", StringComparison.Ordinal)); break;
            case "wrong-test": lane = lane with { RerunFailingTestIdentities = [Fixture.Identities[2]] }; break;
            case "duplicate-test": lane = lane with { RerunTestResultPaths = [fixture.WriteTrx("duplicate.trx", [(Fixture.Identities[0], "Failed"), (Fixture.Identities[0], "Failed")])] }; break;
            case "duplicate-run": lane = lane with { RerunTestResultPaths = lane.FirstTestResultPaths }; break;
            case "nonexecuted-predicate": lane = lane with { RerunPredicate = "zero-tests" }; break;
            case "passing-rerun": File.WriteAllText(path, File.ReadAllText(path).Replace("outcome=\"Failed\"", "outcome=\"Passed\"", StringComparison.Ordinal)); break;
            default: throw new InvalidOperationException(mutation);
        }
        fixture.Check = check with { LaneRerun = lane };
        var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        Assert.Equal(3, reading.FailingTests.Count);
        Assert.All(reading.FailingTests, test =>
        {
            Assert.False(test.CandidateRerunFailed);
            Assert.Null(test.ConfirmedLaneFailure);
        });
        Assert.Equal(fixture.Check.LaneRerun.FirstTestResultPaths, lane.FirstTestResultPaths);
        Assert.All(lane.FirstTestResultPaths, first => Assert.True(File.Exists(first)));
        Assert.IsNotType<ApparatusRedDisposition.AttributionPending>(fixture.Gate.Classify(fixture.Goal, reading));
    }

    [Fact]
    public void SummaryMustIndependentlyBindTheFailedAttemptAndCandidate()
    {
        using var fixture = new Fixture();
        foreach (var summary in new[] {
            fixture.Summary with { AttemptId = null }, fixture.Summary with { AttemptId = "stale" },
            fixture.Summary with { BranchHeadSha = "other-candidate" }, fixture.Summary with { MainHeadSha = "other-main" } })
        {
            var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, summary, new(() => ["src/Changed.cs"]))!;
            Assert.All(reading.FailingTests, test => Assert.Null(test.ConfirmedLaneFailure));
        }
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("NotRunnable")]
    [InlineData("Failed")]
    public void ProducerRejectsNonexecutedOrWrongIdentityRowsEvenWithConfirmedFailureLabel(string outcome)
    {
        using var fixture = new Fixture();
        var lane = fixture.Check.LaneRerun!;
        var path = fixture.WriteTrx("not-proof.trx", [("ForeignTests.OtherCase(value: 1)", outcome)]);
        var check = fixture.Check with { TestResultPaths = lane.FirstTestResultPaths.Append(path).ToArray(),
            LaneRerun = lane with { Attribution = null, RerunTestResultPaths = [path] } };
        Assert.Null(AcceptanceLaneRerunAttributionReader.Attach(check, fixture.Goal.Id.Value, Fixture.Candidate, Fixture.Main).LaneRerun!.Attribution);
    }

    [Theory]
    [InlineData("duplicate-test")]
    [InlineData("duplicate-definition")]
    [InlineData("duplicate-run-id")]
    public void ProducerRejectsAmbiguityBeforeIssuingCustody(string ambiguity)
    {
        using var fixture = new Fixture();
        var lane = fixture.Check.LaneRerun!;
        var path = lane.RerunTestResultPaths.Single();
        var doc = System.Xml.Linq.XDocument.Load(path);
        if (ambiguity == "duplicate-test")
            doc.Root!.Element("Results")!.Add(new System.Xml.Linq.XElement(doc.Descendants("UnitTestResult").First()));
        else if (ambiguity == "duplicate-definition")
            doc.Root!.Add(new System.Xml.Linq.XElement("TestDefinitions",
                new System.Xml.Linq.XElement("UnitTest", new System.Xml.Linq.XAttribute("id", "same")),
                new System.Xml.Linq.XElement("UnitTest", new System.Xml.Linq.XAttribute("id", "same"))));
        else doc.Root!.SetAttributeValue("id", System.Xml.Linq.XDocument.Load(lane.FirstTestResultPaths.Single()).Root!.Attribute("id")!.Value);
        doc.Save(path);
        var check = fixture.Check with { LaneRerun = lane with { Attribution = null } };
        Assert.Null(AcceptanceLaneRerunAttributionReader.Attach(check, fixture.Goal.Id.Value, Fixture.Candidate, Fixture.Main).LaneRerun!.Attribution);
    }

    [Fact]
    public void ExactDataCasesResolveFromDefinitionsWithoutConflatingTheoryRows()
    {
        using var fixture = new Fixture();
        var lane = fixture.Check.LaneRerun!;
        var document = System.Xml.Linq.XDocument.Load(lane.RerunTestResultPaths.Single());
        var rows = document.Descendants("UnitTestResult").ToArray();
        var definitions = new System.Xml.Linq.XElement("TestDefinitions");
        for (var i = 0; i < rows.Length; i++)
        {
            var identity = rows[i].Attribute("testName")!.Value;
            var methodEnd = identity.IndexOf('(');
            var method = identity[0..methodEnd].Split('.').Last();
            var id = $"case-{i}";
            rows[i].SetAttributeValue("testId", id);
            rows[i].SetAttributeValue("testName", "Display" + identity[methodEnd..]);
            definitions.Add(new System.Xml.Linq.XElement("UnitTest", new System.Xml.Linq.XAttribute("id", id),
                new System.Xml.Linq.XElement("TestMethod", new System.Xml.Linq.XAttribute("className", Fixture.ClassName),
                    new System.Xml.Linq.XAttribute("name", method))));
        }
        document.Root!.Add(definitions);
        document.Save(lane.RerunTestResultPaths.Single());
        fixture.Check = AcceptanceLaneRerunAttributionReader.Attach(fixture.Check with { LaneRerun = lane with { Attribution = null } },
            fixture.Goal.Id.Value, Fixture.Candidate, Fixture.Main);
        var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        Assert.Equal(Fixture.Identities.Take(2).Order(StringComparer.Ordinal), reading.FailingTests.Where(test => test.CandidateRerunFailed)
            .Select(test => test.TestIdentity).Order(StringComparer.Ordinal));
        Assert.False(reading.FailingTests.Single(test => test.TestIdentity == Fixture.Identities[2]).CandidateRerunFailed);
    }
}
