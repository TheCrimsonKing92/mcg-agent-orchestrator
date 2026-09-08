using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CleanTestBaselineTests
{
    [Xunit.Theory]
    [Xunit.InlineData("FixturePublication", "NotRun", "RepositoryMissing", false, null, 0, 0, true)]
    [Xunit.InlineData("FixturePublication", "EmptyRequiredOutput", "HeadMissing", true, 0, 0, 0, true)]
    [Xunit.InlineData("FixturePublication", "NonZeroExit", "ReferenceInvalid", true, 128, 0, 0, true)]
    [Xunit.InlineData("FixturePublication", "NonZeroExit", "ValidLooseReference", true, 128, 0, 0, false)]
    [Xunit.InlineData("ProcessOutputApparatus", "ProcessObservationFailure", "ValidLooseReference", true, 0, 0, 31, true)]
    [Xunit.InlineData("ProcessOutputApparatus", "ProcessObservationFailure", "ValidLooseReference", true, 0, 0, 0, false)]
    [Xunit.InlineData("ProcessOutputApparatus", "ProcessObservationFailure", "HeadMissing", true, 0, 0, 31, false)]
    public void SeededRepositoryCauseReceiptDecisionTableRequiresPositiveOwnerEvidence(
        string owner,
        string classification,
        string headState,
        bool processStarted,
        int? exitCode,
        long stdoutBytes,
        long stderrBytes,
        bool expected)
    {
        var receipt = new AcceptanceFailureCauseReceiptV1(
            1,
            "seeded-dispatch-repository-git-probe",
            owner,
            classification,
            processStarted,
            exitCode,
            stdoutBytes,
            stderrBytes,
            false,
            false,
            false,
            headState,
            "TemplateHeadCommit",
            "create-decision-table",
            1);

        Assert.Equal(
            expected,
            AcceptanceFailureCauseReceiptCodec.TryParse(
                AcceptanceFailureCauseReceiptCodec.Format(receipt),
                out _));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, 31, true)]
    [Xunit.InlineData(0, 31, true)]
    [Xunit.InlineData(128, 31, true)]
    [Xunit.InlineData(128, 0, false)]
    public void ProcessObservationReceiptRetainsParentFaultWithAnyChildExit(
        int? exitCode,
        long stderrBytes,
        bool expected)
    {
        var receipt = new AcceptanceFailureCauseReceiptV1(
            ContractVersion: 1,
            Kind: "seeded-dispatch-repository-git-probe",
            Owner: "ProcessOutputApparatus",
            ProbeClassification: "ProcessObservationFailure",
            ProcessStarted: true,
            ExitCode: exitCode,
            StandardOutputByteCount: 0,
            StandardErrorByteCount: stderrBytes,
            DrainTimedOut: false,
            TimedOut: false,
            DrainFailed: false,
            RepositoryHeadState: "ValidLooseReference",
            Check: "PublishedHeadCommit",
            FixtureAttemptId: "create-capture-read-control",
            ProbeOrdinal: 3);

        var parsed = AcceptanceFailureCauseReceiptCodec.TryParse(
            AcceptanceFailureCauseReceiptCodec.Format(receipt),
            out _);

        Assert.Equal(expected, parsed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "LaunchFailure", false, null, false, false, false, true)]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "ProcessTimeout", true, null, true, false, false, true)]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "DrainTimeout", true, 0, false, true, false, true)]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "DrainFailure", true, 0, false, false, true, true)]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "ProcessObservationFailure", true, 128, false, false, false, true)]
    [Xunit.InlineData("FixturePublication", "HeadMissing", "LaunchFailure", false, null, false, false, false, true)]
    [Xunit.InlineData("FixturePublication", "HeadMissing", "ProcessTimeout", true, null, true, false, false, true)]
    [Xunit.InlineData("FixturePublication", "HeadMissing", "DrainTimeout", true, 0, false, true, false, true)]
    [Xunit.InlineData("FixturePublication", "HeadMissing", "DrainFailure", true, 0, false, false, true, true)]
    [Xunit.InlineData("FixturePublication", "HeadMissing", "ProcessObservationFailure", true, 128, false, false, false, true)]
    [Xunit.InlineData("ProcessOutputApparatus", "HeadMissing", "LaunchFailure", false, null, false, false, false, false)]
    [Xunit.InlineData("FixturePublication", "ValidLooseReference", "LaunchFailure", false, null, false, false, false, false)]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "LaunchFailure", true, null, false, false, false, false)]
    [Xunit.InlineData("FixturePublication", "HeadMissing", "ProcessTimeout", true, null, false, false, false, false)]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "DrainTimeout", true, 0, true, true, false, false)]
    [Xunit.InlineData("FixturePublication", "HeadMissing", "DrainFailure", true, 0, false, true, true, false)]
    [Xunit.InlineData("ProcessOutputApparatus", "ValidLooseReference", "ProcessObservationFailure", true, 128, false, false, true, false)]
    public void SeededRepositoryFaultReceiptDecisionTableRequiresCoherentEvidence(
        string owner,
        string headState,
        string classification,
        bool processStarted,
        int? exitCode,
        bool timedOut,
        bool drainTimedOut,
        bool drainFailed,
        bool expected)
    {
        var receipt = new AcceptanceFailureCauseReceiptV1(
            ContractVersion: 1,
            Kind: "seeded-dispatch-repository-git-probe",
            Owner: owner,
            ProbeClassification: classification,
            ProcessStarted: processStarted,
            ExitCode: exitCode,
            StandardOutputByteCount: 0,
            StandardErrorByteCount: 31,
            DrainTimedOut: drainTimedOut,
            TimedOut: timedOut,
            DrainFailed: drainFailed,
            RepositoryHeadState: headState,
            Check: "TemplateHeadCommit",
            FixtureAttemptId: "create-fault-decision-table",
            ProbeOrdinal: 1);

        Assert.Equal(
            expected,
            AcceptanceFailureCauseReceiptCodec.TryParse(
                AcceptanceFailureCauseReceiptCodec.Format(receipt),
                out _));
    }

    [Xunit.Fact]
    public void ResolveCandidatePassRetainsOnlyObservedGreenEvidence()
    {
        var current = GoalId.New();
        var prior = GoalId.New();
        var journals = Journals(
            (prior, Entry(prior, "main-a", "passed")));

        var receipt = CleanTestBaseline.Resolve(journals, current, " MAIN-A ", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["core tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.ObservedGreenCandidatePass, receipt.Attestation);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
        Assert.Contains("does not attest", attribution.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ResolveTwoGoalsSharingFailureRetainsOnlyObservedCorrelation()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["infrastructure tests"])),
            (second, Entry(second, "MAIN-A", "failed", ["infrastructure tests", "other"])));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["infrastructure tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.ObservedRedCorrelation, receipt.Attestation);
        Assert.Equal(["infrastructure tests"], receipt.SharedFailingChecks);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
        Assert.Equal(AcceptanceFailureCause.NotClassified, attribution.Cause);
        Assert.True(
            attribution.Evidence.Contains(first.Value[..8], StringComparison.OrdinalIgnoreCase) ||
            attribution.Evidence.Contains(second.Value[..8], StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void AttributeTypedApparatusReceiptSuppliesCause()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["infrastructure tests"])),
            (second, Entry(second, "main-a", "failed", ["infrastructure tests"])));
        var failedCheckReceipt = new AcceptanceCheckResult(
            "infrastructure tests",
            false,
            1,
            "typed apparatus evidence",
            FailureCauseEvidence: new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus,
                "git child receipt: exit=0; stdoutBytes=0; repositoryHead=valid"));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt,
            [failedCheckReceipt.Name],
            journals,
            current,
            "main-a",
            [failedCheckReceipt]));

        Assert.Equal(
            AcceptanceFailureCause.EnvironmentalApparatus,
            attribution.Cause);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
        Assert.Contains("git child receipt", attribution.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AttributeUsesExecutedMergeBaseIdentityEvidenceForUniformOrigin()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        const string checkName = "infrastructure tests";
        const string identity = "Tests.BaselineFixture.Fails";
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", [checkName])),
            (second, Entry(second, "main-a", "failed", [checkName])));
        var failedCheck = new AcceptanceCheckResult(
            checkName,
            false,
            1,
            "failure",
            FailingTestIdentities: [identity],
            FailingTestAttributions:
            [
                new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Inherited,
                    "same focused identity failed at merge-base base-a")
            ]);

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, [checkName], journals, current, "main-a", [failedCheck]));

        Assert.Equal(AcceptanceFailureOrigin.Inherited, attribution.Origin);
        Assert.Contains("merge-base base-a", attribution.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AttributeMixedMergeBaseIdentityEvidenceRemainsUnattributed()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        const string checkName = "infrastructure tests";
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", [checkName])),
            (second, Entry(second, "main-a", "failed", [checkName])));
        var failedCheck = new AcceptanceCheckResult(
            checkName,
            false,
            1,
            "failure",
            FailingTestIdentities: ["Tests.One", "Tests.Two"],
            FailingTestAttributions:
            [
                new AcceptanceTestFailureAttribution("Tests.One", AcceptanceTestFailureOrigin.Inherited, "failed at merge-base base-a"),
                new AcceptanceTestFailureAttribution("Tests.Two", AcceptanceTestFailureOrigin.Introduced, "green at merge-base base-a")
            ]);

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, [checkName], journals, current, "main-a", [failedCheck]));

        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
        Assert.Contains("correlation does not prove", attribution.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AttributeVerifierClassificationReceiptSuppliesCauseWithExactIdentity()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        const string checkName = "infrastructure tests: Remainder";
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", [checkName])),
            (second, Entry(second, "main-a", "failed", [checkName])));
        var verifierReceipt = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            new AcceptanceCheckResult(
                checkName,
                false,
                1,
                "diagnostic text is not cause evidence",
                FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference));

        var baseline = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            baseline,
            [checkName],
            journals,
            current,
            "main-a",
            [verifierReceipt]));

        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, attribution.Cause);
        Assert.Equal(checkName, verifierReceipt.FailureCauseEvidence?.CheckName);
        Assert.Equal(
            AcceptanceFailureClassifications.GateEnvironmentInterference,
            verifierReceipt.FailureCauseEvidence?.SourceClassification);
        Assert.Contains(checkName, attribution.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void VerifierTrxReceiptSuppliesCauseWithoutInjectedFinalEvidence()
    {
        const string checkName = "infrastructure tests: Remainder";
        var json = """
            {"contractVersion":1,"kind":"seeded-dispatch-repository-git-probe","owner":"ProcessOutputApparatus","probeClassification":"EmptyRequiredOutput","processStarted":true,"exitCode":0,"standardOutputByteCount":0,"standardErrorByteCount":0,"drainTimedOut":false,"timedOut":false,"drainFailed":false,"repositoryHeadState":"ValidLooseReference","check":"PublishedTopLevel","fixtureAttemptId":"create-control","probeOrdinal":7}
            """;
        var marker = "MCG_ACCEPTANCE_CAUSE_V1:" +
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        var trxPath = Path.Combine(Path.GetTempPath(), $"cause-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(
                trxPath,
                $"""
                <TestRun>
                  <Results>
                    <UnitTestResult testId="1" testName="fixture" outcome="Failed">
                      <Output><ErrorInfo><Message>{marker}</Message></ErrorInfo></Output>
                    </UnitTestResult>
                  </Results>
                  <TestDefinitions>
                    <UnitTest id="1" name="fixture"><TestMethod className="Fixture" name="Fails" /></UnitTest>
                  </TestDefinitions>
                </TestRun>
                """);

            var receipt = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
                new AcceptanceCheckResult(
                    checkName,
                    false,
                    1,
                    "ordinary nonempty Remainder failure",
                    TestResultPaths: [trxPath],
                    ExecutedTestCount: 42));

            Assert.Equal(
                "seeded-repository-process-output-apparatus",
                receipt.FailureClassification);
            Assert.Equal(
                AcceptanceFailureCause.EnvironmentalApparatus,
                receipt.FailureCauseEvidence?.Cause);
            Assert.Equal(checkName, receipt.FailureCauseEvidence?.CheckName);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Timeout")]
    [Xunit.InlineData("Aborted")]
    [Xunit.InlineData("Error")]
    public void VerifierMixedApparatusAndUnreceiptedFatalOutcomeFailsClosed(string fatalOutcome)
    {
        const string checkName = "infrastructure tests: Remainder";
        var receipt = new AcceptanceFailureCauseReceiptV1(
            1, "seeded-dispatch-repository-git-probe", "ProcessOutputApparatus",
            "EmptyRequiredOutput", true, 0, 0, 0, false, false, false,
            "ValidLooseReference", "PublishedTopLevel", "create-mixed-outcome", 1);
        var trxPath = Path.Combine(Path.GetTempPath(), $"mixed-outcome-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(
                trxPath,
                $"""
                <TestRun><Results>
                  <UnitTestResult testId="1" outcome="Failed"><Output><ErrorInfo><Message>{AcceptanceFailureCauseReceiptCodec.Format(receipt)}</Message></ErrorInfo></Output></UnitTestResult>
                  <UnitTestResult testId="2" outcome="{fatalOutcome}"><Output><ErrorInfo><Message>ordinary {fatalOutcome} failure</Message></ErrorInfo></Output></UnitTestResult>
                </Results></TestRun>
                """);

            var result = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
                new AcceptanceCheckResult(checkName, false, 1, "mixed failure outcomes", TestResultPaths: [trxPath]));

            Assert.Null(result.FailureClassification);
            Assert.Null(result.FailureCauseEvidence);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Timeout")]
    [Xunit.InlineData("Aborted")]
    [Xunit.InlineData("Error")]
    public void VerifierAllFatalOutcomesWithApparatusReceiptsRemainClassified(string fatalOutcome)
    {
        const string checkName = "infrastructure tests: Remainder";
        var receipt = new AcceptanceFailureCauseReceiptV1(
            1, "seeded-dispatch-repository-git-probe", "ProcessOutputApparatus",
            "EmptyRequiredOutput", true, 0, 0, 0, false, false, false,
            "ValidLooseReference", "PublishedTopLevel", "create-all-apparatus", 1);
        var marker = AcceptanceFailureCauseReceiptCodec.Format(receipt);
        var trxPath = Path.Combine(Path.GetTempPath(), $"all-apparatus-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(
                trxPath,
                $"""
                <TestRun><Results>
                  <UnitTestResult testId="1" outcome="Failed"><Output><ErrorInfo><Message>{marker}</Message></ErrorInfo></Output></UnitTestResult>
                  <UnitTestResult testId="2" outcome="{fatalOutcome}"><Output><ErrorInfo><Message>{marker}</Message></ErrorInfo></Output></UnitTestResult>
                  <UnitTestResult testId="3" outcome="Passed" />
                  <UnitTestResult testId="4" outcome="NotExecuted" />
                </Results></TestRun>
                """);

            var result = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
                new AcceptanceCheckResult(checkName, false, 1, "typed apparatus outcomes", TestResultPaths: [trxPath]));

            Assert.Equal(
                AcceptanceFailureClassifications.SeededRepositoryProcessOutputApparatus,
                result.FailureClassification);
            Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, result.FailureCauseEvidence?.Cause);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Xunit.Fact]
    public void VerifierMixedSeededRepositoryOwnersUseGeneralApparatusClassification()
    {
        const string checkName = "infrastructure tests: Worker dispatch fixtures";
        var processReceipt = new AcceptanceFailureCauseReceiptV1(
            1, "seeded-dispatch-repository-git-probe", "ProcessOutputApparatus",
            "EmptyRequiredOutput", true, 0, 0, 0, false, false, false,
            "ValidLooseReference", "PublishedTopLevel", "create-process", 2);
        var fixtureReceipt = new AcceptanceFailureCauseReceiptV1(
            1, "seeded-dispatch-repository-git-probe", "FixturePublication",
            "NotRun", false, null, 0, 0, false, false, false,
            "GitMetadataMissing", "TemplateMetadata", "create-fixture", 1);
        var trxPath = Path.Combine(Path.GetTempPath(), $"mixed-cause-{Guid.NewGuid():N}.trx");
        try
        {
            var processMarker = AcceptanceFailureCauseReceiptCodec.Format(processReceipt);
            var fixtureMarker = AcceptanceFailureCauseReceiptCodec.Format(fixtureReceipt);
            File.WriteAllText(
                trxPath,
                $"""
                <TestRun><Results>
                  <UnitTestResult testId="1" outcome="Failed"><Output><ErrorInfo><Message>{processMarker}</Message></ErrorInfo></Output></UnitTestResult>
                  <UnitTestResult testId="2" outcome="Failed"><Output><ErrorInfo><Message>{fixtureMarker}</Message></ErrorInfo></Output></UnitTestResult>
                </Results></TestRun>
                """);

            var result = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
                new AcceptanceCheckResult(checkName, false, 1, "typed failures", TestResultPaths: [trxPath]));

            Assert.Equal(AcceptanceFailureClassifications.SeededRepositoryApparatus, result.FailureClassification);
            Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, result.FailureCauseEvidence?.Cause);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Xunit.Fact]
    public void VerifierClassificationReceiptMissingConflictingOrUndefinedCauseFailsClosed()
    {
        const string checkName = "infrastructure tests: Remainder";
        var missing = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            new AcceptanceCheckResult(checkName, false, 1, null));
        var blank = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            new AcceptanceCheckResult(checkName, false, 1, null, FailureClassification: " "));
        var unknown = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            new AcceptanceCheckResult(checkName, false, 1, null, FailureClassification: "unknown-cause"));
        var conflicting = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            new AcceptanceCheckResult(
                checkName,
                false,
                1,
                null,
                FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference,
                FailureCauseEvidence: new AcceptanceFailureCauseEvidence(
                    AcceptanceFailureCause.FixturePublication,
                    "repository bytes invalid")));
        var undefined = GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            new AcceptanceCheckResult(
                checkName,
                false,
                1,
                null,
                FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference,
                FailureCauseEvidence: new AcceptanceFailureCauseEvidence(
                    (AcceptanceFailureCause)int.MaxValue,
                    "undefined")));

        Assert.Null(missing.FailureCauseEvidence);
        Assert.Null(blank.FailureCauseEvidence);
        Assert.Null(unknown.FailureCauseEvidence);
        Assert.Null(conflicting.FailureCauseEvidence);
        Assert.Null(undefined.FailureCauseEvidence);
    }

    [Xunit.Fact]
    public void AttributeConflictingOrMalformedCauseEvidenceRemainsUnclassified()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["infrastructure tests"])),
            (second, Entry(second, "main-a", "failed", ["infrastructure tests"])));
        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var environmental = new AcceptanceCheckResult(
            "infrastructure tests",
            false,
            1,
            null,
            FailureCauseEvidence: new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus,
                "git child receipt"));
        var conflicting = environmental with
        {
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.FixturePublication,
                "repository bytes invalid")
        };
        var malformed = environmental with
        {
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                AcceptanceFailureCause.EnvironmentalApparatus,
                " ")
        };
        var undefined = environmental with
        {
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(
                (AcceptanceFailureCause)int.MaxValue,
                "unknown cause")
        };

        var conflictingAttribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt,
            [environmental.Name],
            journals,
            current,
            "main-a",
            [environmental, conflicting]));
        var malformedAttribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt,
            [environmental.Name],
            journals,
            current,
            "main-a",
            [malformed]));
        var undefinedAttribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt,
            [environmental.Name],
            journals,
            current,
            "main-a",
            [undefined]));

        Assert.Equal(AcceptanceFailureCause.NotClassified, conflictingAttribution.Cause);
        Assert.Equal(AcceptanceFailureCause.NotClassified, malformedAttribution.Cause);
        Assert.Equal(AcceptanceFailureCause.NotClassified, undefinedAttribution.Cause);
    }

    [Xunit.Fact]
    public void ResolveSharedFailureTakesPrecedenceOverPassingCandidateEvidence()
    {
        var current = GoalId.New();
        var passing = GoalId.New();
        var firstFailure = GoalId.New();
        var secondFailure = GoalId.New();
        var journals = Journals(
            (passing, Entry(passing, "main-a", "passed")),
            (firstFailure, Entry(firstFailure, "main-a", "failed", ["core tests"])),
            (secondFailure, Entry(secondFailure, "main-a", "failed", ["core tests"])));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["core tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.ObservedRedCorrelation, receipt.Attestation);
        Assert.Equal(["core tests"], receipt.SharedFailingChecks);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
    }

    [Xunit.Fact]
    public void ResolveRepeatedCandidateLineageRemainsObservedAndUnattributed()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["core tests"], "candidate-a")),
            (second, Entry(second, "main-a", "failed", ["core tests"], "candidate-a")));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["core tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.ObservedRedCorrelation, receipt.Attestation);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
        Assert.Contains("same candidate lineage", receipt.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ResolveDistinctCandidateLineagesRemainObservedWithoutClaimingIdentityIsIncomplete()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["core tests"], "candidate-a")),
            (second, Entry(second, "main-a", "failed", ["core tests"], "candidate-b")));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);

        Assert.Equal(CleanBaselineAttestation.ObservedRedCorrelation, receipt.Attestation);
        Assert.Contains("distinct candidate lineages", receipt.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("incomplete lineage identity", receipt.Evidence, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ResolveLegacyOrInconclusiveEvidenceRemainsUnattested()
    {
        var current = GoalId.New();
        var legacy = GoalId.New();
        var inconclusive = GoalId.New();
        var journals = Journals(
            (legacy, Entry(legacy, "main-a", "unknown", ["core tests"])),
            (inconclusive, Entry(inconclusive, "main-a", "failed", null)));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["core tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.Unattested, receipt.Attestation);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
    }

    [Xunit.Fact]
    public void ResolveSingleGoalOrDifferentMainRemainsUnattested()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["core tests"])),
            (second, Entry(second, "main-b", "failed", ["core tests"])));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);

        Assert.Equal(CleanBaselineAttestation.Unattested, receipt.Attestation);
        Assert.Equal(
            AcceptanceFailureOrigin.Unattributed,
            Assert.Single(CleanTestBaseline.Attribute(receipt, ["core tests"], journals, current, "main-a")).Origin);
    }

    [Xunit.Fact]
    public void AcceptanceFailureJournalPersistsFailedChecksAndIgnoresMalformedLines()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-clean-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var goal = TestGoal("Baseline journal");
            GoalOperationJournal.AcceptanceFailed(
                root,
                goal,
                "conductor:acceptance",
                "branch-a",
                "main-a",
                failedCheckNames: ["core tests"]);
            GoalOperationJournal.Begin(
                root,
                goal,
                "conductor:clean-baseline",
                "resolving",
                "main-a");
            GoalOperationJournal.Completed(
                root,
                goal,
                "conductor:clean-baseline",
                "attestation=unattested",
                "main-a");
            var path = Path.Combine(root, ".orchestrator", "goal-operations", $"{goal.Id.Value}.jsonl");
            SharedJsonlFile.AppendLine(path, "{malformed");

            var journal = GoalOperationJournal.ReadAll(root)[goal.Id];

            var acceptance = Assert.Single(journal.Entries.Where(entry => entry.AcceptanceOutcome == "failed"));
            Assert.Equal(["core tests"], acceptance.FailedCheckNames);
            Assert.Equal("branch-a", acceptance.BranchHeadSha);
            var baseline = Assert.Single(journal.LatestByOperation.Where(entry =>
                entry.Operation == "conductor:clean-baseline"));
            Assert.Equal("main-a", baseline.MainHeadSha);
            Assert.Equal(GoalOperationStatus.Completed, baseline.Status);

            var evidence = Assert.Single(GoalOperationJournal.ReadAcceptanceEvidenceForMain(root, " MAIN-A "));
            Assert.Equal(goal.Id, evidence.GoalId);
            Assert.Equal("failed", evidence.AcceptanceOutcome);
            Assert.Equal(["core tests"], evidence.FailedCheckNames);
            Assert.Equal("branch-a", evidence.BranchHeadSha);
            Assert.Empty(GoalOperationJournal.ReadAcceptanceEvidenceForMain(root, "main-b"));
            Assert.Empty(GoalOperationJournal.ReadAcceptanceEvidenceForMain(root, "   "));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ReconcileAttentionResolvesRedBaselineItemAfterMainMoves()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-clean-baseline-attention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = CollaborationItemStore.ForDirectory(Path.Combine(root, ".orchestrator"));
            var goal = TestGoal("Baseline attention");
            var red = new CleanTestBaselineReceipt(
                "main-a",
                null,
                CleanBaselineAttestation.ObservedRedCorrelation,
                goal.Id.Value,
                DateTimeOffset.UtcNow,
                ["core tests"],
                "shared failure");
            var green = new CleanTestBaselineReceipt(
                "main-b",
                null,
                CleanBaselineAttestation.ObservedGreenCandidatePass,
                goal.Id.Value,
                DateTimeOffset.UtcNow,
                [],
                "passing receipt");

            ConductorDriver.ReconcileCleanBaselineAttention(store, goal, "main-a", red);
            ConductorDriver.ReconcileCleanBaselineAttention(store, goal, "main-b", green);

            var item = Assert.Single(await store.ListAsync());
            Assert.Equal("clean-baseline-red:main-a", item.CorrelationKey);
            Assert.Equal("Observed clean-test failure correlation at main-a", item.Subject);
            Assert.Equal(CollaborationItemStatus.Resolved, item.Status);
            Assert.Contains("main-b", item.Resolution, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> Journals(
        params (GoalId GoalId, GoalOperationJournalEntry Entry)[] values) =>
        values
            .GroupBy(value => value.GoalId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var entries = group.Select(value => value.Entry).ToArray();
                    return new GoalOperationJournalSummary("journal", entries, entries, []);
                });

    private static Goal TestGoal(string objective) =>
        new(
            GoalId.New(),
            objective,
            [new TaskSpec(TaskId.New(), "Exercise baseline behavior", AgentRole.Developer)]);

    private static GoalOperationJournalEntry Entry(
        GoalId goalId,
        string mainSha,
        string outcome,
        IReadOnlyList<string>? failedChecks = null,
        string? branchHeadSha = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            goalId,
            "conductor:acceptance",
            outcome == "passed" ? GoalOperationStatus.Completed : GoalOperationStatus.Failed,
            DateTimeOffset.UtcNow,
            "receipt",
            BranchHeadSha: branchHeadSha,
            MainHeadSha: mainSha,
            AcceptanceOutcome: outcome,
            FailedCheckNames: failedChecks);
}
