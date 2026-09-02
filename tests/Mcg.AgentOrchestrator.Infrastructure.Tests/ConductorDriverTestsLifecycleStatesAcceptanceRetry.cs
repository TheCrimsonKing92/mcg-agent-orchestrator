using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Xml.Linq;

using static ConductorDriverTests;

public sealed partial class ConductorDriverTestsLifecycleStates
{
    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void IdentityWithoutInheritedProofRetries(bool explicitUnattributed)
    {
        const string identity =
            "Mcg.AgentOrchestrator.Infrastructure.Tests.CliCommandTestsBacklogIntakeCommands.CliBacklogListSplitsLimitStatusAndTextFlags";
        IReadOnlyList<AcceptanceTestFailureAttribution> testAttributions = explicitUnattributed
            ?
            [
                new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Unattributed,
                    "baseline identity evidence unavailable")
            ]
            :
            [];
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: Cli",
            false,
            1,
            "candidate failure without inherited proof",
            FailingTestIdentities: [identity],
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions: testAttributions);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                false,
                [unmet],
                FailedChecks: [unmet.Name],
                BranchHeadSha: "candidate-a",
                MainHeadSha: "main-a",
                CheckAttributions:
                [
                    new AcceptanceCheckAttribution(
                        unmet.Name,
                        AcceptanceFailureOrigin.Inherited,
                        "same check name also failed on main; identity attribution is unproven")
                ]),
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                retryCalled = true;
                Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                return kernel.RetryTask(
                    goalId,
                    taskId,
                    message,
                    retryRoundKind: roundKind,
                    retryCause: cause);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ =>
            [
                "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Backlog.cs"
            ]);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.True(retryCalled);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unattributed_required_check_without_identity_retries")]
    public void UnattributedRequiredCheckWithoutIdentityRetries()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var unmet = new AcceptanceCheckResult(
            "git diff whitespace",
            false,
            1,
            "src/Foo.cs:12: trailing whitespace",
            ResultSummary: "whitespace errors found");
        var baseline = CleanTestBaseline.Resolve(
            Array.Empty<CleanTestBaselineEvidence>(),
            goal.Id,
            "main-a",
            mergeBaseSha: null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            baseline,
            [unmet.Name],
            Array.Empty<CleanTestBaselineEvidence>(),
            goal.Id,
            "main-a",
            [unmet]));
        Assert.Equal(CleanBaselineAttestation.Unattested, baseline.Attestation);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                false,
                [unmet],
                FailedChecks: [unmet.Name],
                BranchHeadSha: "candidate-a",
                MainHeadSha: "main-a",
                CheckAttributions: [attribution],
                BaselineAttestation: CleanTestBaseline.FormatFailureAttestation(baseline)),
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                retryCalled = true;
                Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                return kernel.RetryTask(
                    goalId,
                    taskId,
                    message,
                    retryRoundKind: roundKind,
                    retryCause: cause);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.True(retryCalled);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_retry_feedback_preserves_timeout_trx_when_allowed_identities_are_empty")]
    public void ConductorDriverRetryFeedbackPreservesTimeoutTrxWhenAllowedIdentitiesAreEmpty()
    {
        var tempDirectory = CreateTempDirectory();
        var trxPath = Path.Combine(tempDirectory, "timeout-only.trx");
        try
        {
            new XDocument(
                new XElement(
                    "TestRun",
                    new XElement(
                        "TestDefinitions",
                        new XElement(
                            "UnitTest",
                            new XAttribute("id", "timeout-1"),
                            new XAttribute("name", "TimeoutOnlyTests.Expires"),
                            new XElement(
                                "TestMethod",
                                new XAttribute("className", "TimeoutOnlyTests"),
                                new XAttribute("name", "Expires")))),
                    new XElement(
                        "Results",
                        new XElement(
                            "UnitTestResult",
                            new XAttribute("testId", "timeout-1"),
                            new XAttribute("testName", "TimeoutOnlyTests.Expires"),
                            new XAttribute("outcome", "Timeout"),
                            new XElement(
                                "Output",
                                new XElement(
                                    "ErrorInfo",
                                    new XElement("Message", "30 second partition timeout")))))))
                .Save(trxPath);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var unmet = new AcceptanceCheckResult(
                "infrastructure tests: timeout-only",
                false,
                1,
                "partition timed out",
                ResultSummary: "infrastructure test partition failed",
                TestResultPaths: [trxPath],
                FailingTestIdentities: []);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retryMessage = message;
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    return kernel.RetryTask(
                        goalId,
                        taskId,
                        message,
                        retryRoundKind: roundKind,
                        retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Contains("TimeoutOnlyTests.Expires (Timeout)", retryMessage!, StringComparison.Ordinal);
            Assert.Contains("30 second partition timeout", retryMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("contained no attributable non-passing results", retryMessage!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void OmittedAttributionIdentityReachesDeveloperFeedback()
    {
        var tempDirectory = CreateTempDirectory();
        var trxPath = Path.Combine(tempDirectory, "attribution-cap.trx");
        try
        {
            var identities = Enumerable
                .Range(0, GoalAcceptanceVerifier.MaxFailureAttributionFocusedEvidenceIdentities + 1)
                .Select(index => $"Example.Tests.AttributionCapTests.Failure{index:D2}")
                .ToArray();
            AcceptanceFailureAttributionTestFixtures.WriteFailedTrx(
                trxPath,
                identities.Select(identity => (identity, identity)).ToArray());
            var attributions = identities
                .Take(GoalAcceptanceVerifier.MaxFailureAttributionFocusedEvidenceIdentities)
                .Select(identity => new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Inherited,
                    "same focused identity failed at merge-base main-a"))
                .Append(new AcceptanceTestFailureAttribution(
                    identities[^1],
                    AcceptanceTestFailureOrigin.Unattributed,
                    "merge-base focused attribution omitted by deterministic cap"))
                .ToArray();
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var unmet = new AcceptanceCheckResult(
                "infrastructure tests: attribution cap",
                false,
                1,
                "candidate failures exceeded attribution evidence cap",
                TestResultPaths: [trxPath],
                FailingTestIdentities: identities,
                FailingTestAttributions: attributions);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                    false,
                    [unmet],
                    FailedChecks: [unmet.Name],
                    CheckAttributions:
                    [
                        new AcceptanceCheckAttribution(
                            unmet.Name,
                            AcceptanceFailureOrigin.Inherited,
                            "same check name also failed on main")
                    ]),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retryMessage = message;
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    return kernel.RetryTask(
                        goalId,
                        taskId,
                        message,
                        retryRoundKind: roundKind,
                        retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Contains(identities[^1], retryMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain(identities[0], retryMessage!, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void RetryFeedbackFallsBackWhenTrxMetadataCannotMatchIdentity()
    {
        var tempDirectory = CreateTempDirectory();
        var trxPath = Path.Combine(tempDirectory, "missing-test-method.trx");
        try
        {
            const string canonicalIdentity = "Example.Tests.MetadataTests.CandidateRegression";
            const string fallbackEvidence =
                "[xUnit.net 00:00:00.01]     Example.Tests.MetadataTests.CandidateRegression [FAIL]";
            new XDocument(
                new XElement(
                    "TestRun",
                    new XElement(
                        "TestDefinitions",
                        new XElement(
                            "UnitTest",
                            new XAttribute("id", "missing-metadata-1"),
                            new XAttribute("name", "candidate regression display name"))),
                    new XElement(
                        "Results",
                        new XElement(
                            "UnitTestResult",
                            new XAttribute("testId", "missing-metadata-1"),
                            new XAttribute("testName", "candidate regression display name"),
                            new XAttribute("outcome", "Failed"),
                            new XElement(
                                "Output",
                                new XElement(
                                    "ErrorInfo",
                                    new XElement("Message", "metadata-free failure")))))))
                .Save(trxPath);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var unmet = new AcceptanceCheckResult(
                "infrastructure tests: missing metadata",
                false,
                1,
                fallbackEvidence,
                ResultSummary: "infrastructure test partition failed",
                TestResultPaths: [trxPath],
                FailingTestIdentities: [canonicalIdentity]);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => new AcceptanceVerificationSummary(
                    false,
                    [unmet],
                    FailedChecks: [unmet.Name],
                    CheckAttributions:
                    [
                        new AcceptanceCheckAttribution(
                            unmet.Name,
                            AcceptanceFailureOrigin.Introduced,
                            "main is attested green")
                    ]),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retryMessage = message;
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    return kernel.RetryTask(
                        goalId,
                        taskId,
                        message,
                        retryRoundKind: roundKind,
                        retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Contains(fallbackEvidence, retryMessage!, StringComparison.Ordinal);
            Assert.Contains("contained no attributable non-passing results", retryMessage!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_inherited_failure_without_identity_proof_retries_worker")]
    public void InheritedFailureWithoutIdentityProofRetriesWorker()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var rawAcceptance = new AcceptanceVerificationSummary(
            false,
            [new AcceptanceCheckResult("infrastructure tests: Remainder", false, 1, "inherited red")],
            FailedChecks: ["infrastructure tests: Remainder"],
            BranchHeadSha: "candidate-a",
            MainHeadSha: "main-a",
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    "infrastructure tests: Remainder",
                    AcceptanceFailureOrigin.Inherited,
                    "baseline red without an apparatus receipt")
            ],
            BaselineAttestation: "attested-red");
        var acceptance = ConductorDriver.ClassifyInheritedBaselineApparatus(rawAcceptance);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => acceptance,
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                retryCalled = true;
                Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                return kernel.RetryTask(
                    goalId,
                    taskId,
                    message,
                    retryRoundKind: roundKind,
                    retryCause: cause);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.True(retryCalled);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
    }
}
