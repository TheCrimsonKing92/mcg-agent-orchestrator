using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Unique repositories, databases and coordinator roots; JobAccounting isolates stable build-slot leases.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedEvidenceProducerRouting
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Theory]
    [InlineData("green", FindingEvidenceArmDisposition.Green, FindingEvidenceOutcomeReason.ValidEvidence)]
    [InlineData("red", FindingEvidenceArmDisposition.Red, FindingEvidenceOutcomeReason.CandidateRed)]
    [InlineData("zero", FindingEvidenceArmDisposition.ApparatusFailure, FindingEvidenceOutcomeReason.ApparatusFailure)]
    [InlineData("unreadable", FindingEvidenceArmDisposition.ApparatusFailure, FindingEvidenceOutcomeReason.ApparatusFailure)]
    [InlineData("build-failure", FindingEvidenceArmDisposition.Inconclusive, FindingEvidenceOutcomeReason.CandidateInconclusive)]
    public async Task CandidateOnlyExecutionRecordsItsActualDisposition(
        string scenario, FindingEvidenceArmDisposition disposition, FindingEvidenceOutcomeReason reason)
    {
        var root = CreateCandidate();
        try
        {
            var calls = new List<string[]>();
            var result = await CreateVerifier(calls, scenario).RunFocusedEvidenceAsync(
                root, GoalId.New(), "Infrastructure.Tests: ProducerFirstTests");

            Assert.True(result.Accepted);
            Assert.Equal(reason, result.OutcomeReason);
            Assert.Equal(scenario == "green", result.Passed);
            var arm = Assert.Single(result.Arms!);
            Assert.Equal(FindingEvidenceArm.Candidate, arm.Arm);
            Assert.Equal(CandidateSha(root), arm.Sha);
            Assert.Equal(disposition, arm.Disposition);
            Assert.DoesNotContain(result.Arms!, item => item.Arm == FindingEvidenceArm.Baseline);
            Assert.NotEmpty(calls);
            if (scenario != "build-failure")
                AssertSelectedCommand(calls, "ProducerFirstTests");
            if (scenario == "red")
                Assert.Contains(arm.Checks.SelectMany(check => check.FailingTestIdentities ?? []),
                    identity => identity.Contains("ProducerFirstTests.Executed", StringComparison.Ordinal));
            if (scenario == "zero")
                Assert.Equal(0, Assert.Single(arm.Checks).ExecutedTestCount);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task UnmatchedSelectionCannotProduceSuccessfulEvidence()
    {
        var root = CreateCandidate();
        try
        {
            var calls = new List<string[]>();
            var result = await CreateVerifier(calls).RunFocusedEvidenceAsync(
                root, GoalId.New(), "Infrastructure.Tests: FullyQualifiedName~MissingTests.MissingMethod");
            Assert.False(result.Accepted);
            Assert.False(result.Passed);
            Assert.NotEqual(FindingEvidenceOutcomeReason.ValidEvidence, result.OutcomeReason);
            Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, result.Rejection?.Code);
            Assert.Empty(calls);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task GreenProducerSurvivesReloadAndDeliversQueuedEvidenceToTester()
    {
        var root = CreateCandidate();
        try
        {
            var candidateSha = CandidateSha(root);
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            PassVerification(kernel, goal, developer, hasCommittedChanges: true);
            // Reloaded ownership uses the persisted dispatch commit, not the transient verification flag.
            kernel.RecordDispatchResultCommit(goal.Id, developer.Id, candidateSha);
            DispatchTask(kernel, goal, tester, "test", baseCommit: candidateSha);
            RecordFindings(kernel, goal, tester,
                EvidenceFindingWithRequest("Inspect coverage with the produced receipt", "producer-first",
                    FindingCategory.TestCoverage, classes: ["ProducerFirstTests"]),
                EvidenceFindingWithRequest("Queued evidence", "producer-second",
                    FindingCategory.TestEvidence, project: "Core.Tests", classes: ["ProducerSecondTests"]));

            var calls = new List<string[]>();
            var produced = new List<FocusedEvidenceRunResult>();
            var retried = new List<TaskId>();
            var messages = new List<string>();
            ConductorDriver Driver() => MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(root, "attempts"), TrunkBranchName.Default,
                    runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    var result = CreateVerifier(calls).RunFocusedEvidenceAsync(root, goal.Id, request)
                        .GetAwaiter().GetResult();
                    produced.Add(result);
                    return result;
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried.Add(taskId);
                    messages.Add(message);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            var driver = Driver();
            for (var tick = 0; tick < 5 && produced.Count == 0; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var first = Assert.Single(produced);
            Assert.True(first.Passed);
            Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, first.OutcomeReason);
            Assert.Empty(retried); // The second request is still pending: reload before delivery.
            AssertSelectedCommand(calls, "ProducerFirstTests");
            var finding = tester.LastVerification!.MergedReviewFindings!
                .Single(item => item.StableId == "producer-first");
            Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, finding.EvidenceOutcome!.ResultReason);
            var receipt = Assert.Single(tester.LastVerification.FindingEvidenceReceipts!);
            Assert.Equal(finding.EvidenceOutcome.ReceiptId, receipt.ReceiptId);
            Assert.Equal(candidateSha, receipt.CandidateSha);
            var receiptArm = Assert.Single(receipt.Arms!);
            Assert.Equal(FindingEvidenceArm.Candidate, receiptArm.Arm);
            Assert.Equal(1, receiptArm.ExecutedTestCount);
            Assert.NotEmpty(receiptArm.ReceiptArtifacts!);
            Assert.All(receiptArm.TestResultPaths!, path => Assert.True(File.Exists(path), path));
            Assert.True(AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                receiptArm.TestResultPaths, receiptArm.ReceiptArtifacts,
                receiptArm.ExecutedTestCount!.Value, ["ProducerFirstTests"]));
            Assert.Equal("executed-standalone", Assert.Single(receipt.RequestDispositions!,
                item => item.FindingStableId == "producer-first").Disposition);
            Assert.Equal("pending-standalone", Assert.Single(receipt.RequestDispositions!,
                item => item.FindingStableId == "producer-second").Disposition);
            var serializedReceipt = JsonSerializer.Serialize(receipt);

            var repository = CreateMigratedStateRepository(Path.Combine(root, "state.db"));
            await repository.SaveAsync(kernel);
            kernel = await new SqliteOrchestratorStateRepository(Path.Combine(root, "state.db")).LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            developer = kernel.GetTask(goal.Id, developer.Id);
            tester = kernel.GetTask(goal.Id, tester.Id);
            Assert.Equal(candidateSha, developer.LastDispatch!.ResultCommit);
            Assert.Contains(developer.DispatchHistory, dispatch => dispatch.ResultCommit == candidateSha);
            Assert.Equal(serializedReceipt,
                JsonSerializer.Serialize(Assert.Single(tester.LastVerification!.FindingEvidenceReceipts!)));
            Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, tester.LastVerification.MergedReviewFindings!
                .Single(item => item.StableId == "producer-first").EvidenceOutcome!.ResultReason);
            Assert.Equal(FindingEvidenceExecutionState.PendingExecution,
                FindingEvidenceExecutionClassifier.Classify(tester,
                    tester.LastVerification.MergedReviewFindings!.Single(item => item.StableId == "producer-second"),
                    candidateSha));
            var reloadedArm = Assert.Single(Assert.Single(tester.LastVerification.FindingEvidenceReceipts!).Arms!);
            Assert.True(AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                reloadedArm.TestResultPaths, reloadedArm.ReceiptArtifacts,
                reloadedArm.ExecutedTestCount!.Value, ["ProducerFirstTests"]));

            driver = Driver(); // Fresh conductor and coordinator must consume the persisted typed outcome.
            for (var tick = 0; tick < 5 && retried.Count == 0; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(2, produced.Count);
            Assert.All(produced, result => Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, result.OutcomeReason));
            AssertSelectedCommand(calls, "ProducerSecondTests");
            Assert.Equal(2, calls.Count(IsSelectedTestCommand));
            Assert.Equal([tester.Id], retried);
            Assert.DoesNotContain(developer.Id, retried);
            Assert.Contains("route=tester", Assert.Single(messages), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void RecordFindings(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec tester, params ReviewFinding[] findings)
    {
        var stdout = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
            "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
            $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []", "verdict: needs-work",
            "model_fit: fixture/model - adequate - source verification", "skills: none", "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test", "C:\\tmp", 0, stdout, "", DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\tester.out.log", WorkerResultPresent: true));
    }

    private static string CreateCandidate()
    {
        var root = CreateManifestWorkspace("""
            { "version": 1, "checks": [], "forbiddenChangedPathGlobs": [] }
            """);
        foreach (var (project, name) in new[]
        {
            ("Mcg.AgentOrchestrator.Infrastructure.Tests", "ProducerFirstTests"),
            ("Mcg.AgentOrchestrator.Core.Tests", "ProducerSecondTests")
        })
        {
            var sourceRoot = Path.Combine(root, "tests", project);
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(Path.Combine(sourceRoot, name + ".cs"),
                $"namespace Sample.Tests; public sealed class {name} {{ [Xunit.Fact] public void Executed() {{ }} }}");
        }
        Git(root, "init");
        Git(root, "config", "user.name", "Fixture");
        Git(root, "config", "user.email", "fixture@example.invalid");
        Git(root, "add", ".");
        Git(root, "commit", "-m", "candidate fixture");
        return root;
    }

    private static string CandidateSha(string root) => Git(root, "rev-parse", "HEAD").Trim();

    private static string Git(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded, result.Error);
        return result.Output;
    }

    private static void AssertSelectedCommand(List<string[]> calls, string testClass)
    {
        var call = Assert.Single(calls.Where(call =>
            IsSelectedTestCommand(call) &&
            call.Any(argument => argument.Contains(testClass, StringComparison.Ordinal))));
        Assert.Contains("--filter-class", call);
    }

    private static bool IsSelectedTestCommand(string[] args) =>
        IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests") ||
        IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Core.Tests");

    private static GoalAcceptanceVerifier CreateVerifier(List<string[]> calls, string scenario = "green") =>
        new((args, _, _, _) =>
        {
            calls.Add(args);
            if (!IsSelectedTestCommand(args))
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                    scenario == "build-failure" ? 1 : 0, "fixture build"));

            var identities = args.Select((argument, index) => (argument, index))
                .Where(pair => pair.argument == "--filter-class")
                .Select(pair => args[pair.index + 1].Trim('*') + ".Executed").ToArray();
            Assert.NotEmpty(identities);
            WriteMtpTrx(args, scenario == "zero" ? 0 : identities.Length, identities);
            var trxPath = Path.Combine(args[Array.IndexOf(args, "--results-directory") + 1],
                args[Array.IndexOf(args, "--report-trx-filename") + 1]);
            if (scenario == "unreadable")
                File.WriteAllText(trxPath, "<malformed");
            else
            {
                var trx = XDocument.Load(trxPath);
                if (scenario == "red")
                {
                    foreach (var result in trx.Descendants("UnitTestResult"))
                        result.SetAttributeValue("outcome", "Failed");
                    var counters = trx.Descendants("Counters").Single();
                    counters.SetAttributeValue("passed", 0);
                    counters.SetAttributeValue("failed", identities.Length);
                }
                // Match managed-runner TRX: conductor reuse validates the standard namespace.
                XNamespace trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                foreach (var element in trx.Root!.DescendantsAndSelf())
                    element.Name = trxNamespace + element.Name.LocalName;
                trx.Save(trxPath);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(
                scenario == "red" ? 1 : 0, "fixture selected execution"));
        });
}
