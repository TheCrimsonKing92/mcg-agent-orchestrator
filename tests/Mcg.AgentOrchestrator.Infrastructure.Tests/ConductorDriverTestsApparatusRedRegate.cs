using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Xml.Linq;

using static ConductorDriverTests;

/// <summary>
/// The apparatus-RED classification contract at the acceptance failure handler: a RED whose failing
/// tests all lie outside the candidate's changed paths, and that carries either a recorded
/// infrastructure signature or a prior cross-goal occurrence, re-gates instead of reopening a paid
/// Developer round. Everything else keeps today's path.
/// </summary>
public sealed class ConductorDriverTestsApparatusRedRegate
{
    private const string HeartbeatIdentity =
        "Mcg.AgentOrchestrator.Infrastructure.Tests.WorkerDispatchHostTests.HeartbeatObserverTimingRace";
    private const string CanaryIdentity =
        "Mcg.AgentOrchestrator.Infrastructure.Tests.PostLandingCanaryTests.PostLandingCanaryStaysGreen";
    private const string CandidateIdentity =
        "Mcg.AgentOrchestrator.Infrastructure.Tests.ProviderCommandBuilderTests.BuildsWorkerCommand";
    private const string SlotsBusyMessage = "Stable dotnet build slots busy for goal bf434fdb.";

    [Fact(DisplayName = "ConductorDriver_b2f52d39_shape_regates_on_prior_cross_goal_failure")]
    public void PriorCrossGoalFailureOutsideChangedPathsRegatesWithoutDeveloperRetry()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteTestSource(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchHostTests.cs");
            var now = DateTimeOffset.Parse("2026-09-05T14:28:00Z", null);
            var index = CreateIndex(root);

            // The same identity already failed another goal's gate inside the window, on an attempt
            // whose own diff did not touch it. That is the only cross-goal evidence available here:
            // the heartbeat race carries no recorded infrastructure signature.
            index.Append(
                [
                    new AcceptanceFailingTestIndexRecord(
                        AcceptanceFailingTestIndex.ContractVersion,
                        AcceptanceFailingTestIndexKinds.GateFailure,
                        "bf434fdbaaaa4a7987c4b843d1ea9c21",
                        now.AddHours(-6),
                        CheckName: "infrastructure tests: dispatch host",
                        TestIdentity: HeartbeatIdentity,
                        ResolvedSourcePath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchHostTests.cs",
                        InsideChangedPaths: false)
                ],
                now.AddHours(-6));

            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var retryCalled = false;
            var acceptanceFailureRecorded = false;
            var acceptanceRuns = 0;
            var unmet = FailingCheck(
                "infrastructure tests: dispatch host",
                HeartbeatIdentity,
                outputTail: "heartbeat observer did not see the second beat before the 200ms deadline");
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ =>
                {
                    acceptanceRuns++;
                    return RedSummary(unmet);
                },
                retryTaskWithCause: (_, _, _, _, _) =>
                {
                    retryCalled = true;
                    throw new InvalidOperationException("An apparatus RED must not reopen a Developer.");
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                recordAcceptanceFailure: (_, _, _, _, _, _) => acceptanceFailureRecorded = true,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Providers/ProviderCommandBuilder.cs"],
                apparatusRedGate: CreateGate(root, index, now));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains(ApparatusRedClassifier.CrossGoalEvidenceKind, held.Reason, StringComparison.Ordinal);
            Assert.False(retryCalled);
            Assert.False(acceptanceFailureRecorded);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(1, acceptanceRuns);

            // The decisive assertion: a hold that does not re-gate is a permanent park, which is
            // strictly worse than the paid retry it replaces.
            var second = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal(2, acceptanceRuns);
            Assert.IsType<ConductorAdvanceOutcome.Held>(second.Outcome);
            Assert.False(retryCalled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "ConductorDriver_bf434fdb_shape_regates_on_infrastructure_exception_and_journals_it")]
    public void InfrastructureExceptionOutsideChangedPathsRegatesAndJournalsClassification()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteTestSource(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PostLandingCanaryTests.cs");
            var trxPath = Path.Combine(root, "canary.trx");
            WriteSlotsBusyTrx(trxPath, CanaryIdentity);
            var now = DateTimeOffset.Parse("2026-09-05T15:07:00Z", null);
            var index = CreateIndex(root);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var retryCalled = false;
            var unmet = FailingCheck(
                "infrastructure tests: post-landing canary",
                CanaryIdentity,
                outputTail: "canary partition failed",
                trxPath: trxPath);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(unmet),
                retryTaskWithCause: (_, _, _, _, _) =>
                {
                    retryCalled = true;
                    throw new InvalidOperationException("An apparatus RED must not reopen a Developer.");
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals.cs"],
                executionDirectory: root,
                // No prior census entry is seeded: the classification must come from the recorded
                // infrastructure-exception set alone.
                apparatusRedGate: CreateGate(root, index, now));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.False(retryCalled);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);

            var entry = Assert.Single(GoalOperationJournal
                .Read(root, goal.Id)
                .Entries
                .Where(candidate => candidate.Operation.Equals(
                    GoalOperationJournal.AcceptanceApparatusRegateOperation,
                    StringComparison.Ordinal)));
            Assert.Equal(GoalOperationJournal.ApparatusRegateAcceptanceOutcome, entry.AcceptanceOutcome);
            Assert.Contains("infrastructure tests: post-landing canary", entry.FailedCheckNames!);
            Assert.Contains("classification=apparatus", entry.Detail!, StringComparison.Ordinal);
            Assert.Contains(
                $"evidence={ApparatusInfrastructureSignatures.EvidenceKind}",
                entry.Detail!,
                StringComparison.Ordinal);
            Assert.Contains(CanaryIdentity, entry.Detail!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "ConductorDriver_failing_test_inside_changed_paths_reopens_developer_as_today")]
    public void FailingTestInsideChangedPathsReopensDeveloperExactlyAsToday()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            const string sourcePath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProviderCommandBuilderTests.cs";
            WriteTestSource(root, sourcePath);
            var trxPath = Path.Combine(root, "candidate.trx");

            // The same recorded infrastructure exception as the re-gate fact above: the changed-paths
            // boundary must dominate the exception set, not the other way round.
            WriteSlotsBusyTrx(trxPath, CandidateIdentity);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var unmet = FailingCheck(
                "infrastructure tests: providers",
                CandidateIdentity,
                outputTail: "provider command assertion failed",
                trxPath: trxPath);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(unmet),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retryMessage = message;
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => [sourcePath],
                apparatusRedGate: CreateGate(root, CreateIndex(root), DateTimeOffset.Parse("2026-09-05T16:42:00Z", null)));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(1, task.CriterionRetryCount);
            Assert.StartsWith(
                "Acceptance criteria unmet; retrying task with feedback (attempt 1/",
                retryMessage!,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "ConductorDriver_unknown_changed_paths_keep_apparatus_shaped_red_genuine")]
    public void UnknownChangedPathsKeepApparatusShapedRedGenuine()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteTestSource(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PostLandingCanaryTests.cs");
            var trxPath = Path.Combine(root, "canary.trx");
            WriteSlotsBusyTrx(trxPath, CanaryIdentity);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var retryCalled = false;
            var unmet = FailingCheck(
                "infrastructure tests: post-landing canary",
                CanaryIdentity,
                outputTail: "canary partition failed",
                trxPath: trxPath);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(unmet),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retryCalled = true;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                // The candidate/apparatus boundary is unknown, so the classifier must not guess.
                getLandingFileScopes: _ => [],
                apparatusRedGate: CreateGate(root, CreateIndex(root), DateTimeOffset.Parse("2026-09-05T16:42:00Z", null)));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.True(retryCalled);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "ConductorDriver_apparatus_regate_bound_escalates_with_a_distinct_reason")]
    public void ApparatusRegateBoundEscalatesInsteadOfRegatingAgain()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteTestSource(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PostLandingCanaryTests.cs");
            var trxPath = Path.Combine(root, "canary.trx");
            WriteSlotsBusyTrx(trxPath, CanaryIdentity);
            var now = DateTimeOffset.Parse("2026-09-05T15:07:00Z", null);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var retryCalled = false;
            var unmet = FailingCheck(
                "infrastructure tests: post-landing canary",
                CanaryIdentity,
                outputTail: "canary partition failed",
                trxPath: trxPath);
            ConductorDriver MakeBoundedDriver() => MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(unmet),
                retryTaskWithCause: (_, _, _, _, _) =>
                {
                    retryCalled = true;
                    throw new InvalidOperationException("Bound exhaustion must escalate, never reopen a Developer.");
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals.cs"],
                apparatusRedGate: CreateGate(root, CreateIndex(root), now, perGoalRegateCap: 1));

            var first = MakeBoundedDriver().AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);

            // A second driver over the same store proves the count is read from the durable per-goal
            // re-gate records, not from in-memory state.
            var second = MakeBoundedDriver().AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(second.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, escalated.State);
            Assert.Contains(ApparatusRedClassifier.BoundExhaustedToken, escalated.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("retrying task with feedback", escalated.Reason, StringComparison.Ordinal);
            Assert.False(retryCalled);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceFailingTestIndex CreateIndex(string root) =>
        new(Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    private static ApparatusRedGate CreateGate(
        string root,
        AcceptanceFailingTestIndex index,
        DateTimeOffset now,
        int perGoalRegateCap = ApparatusRedGate.DefaultPerGoalRegateCap) =>
        new(index, _ => root, () => now, perGoalRegateCap: perGoalRegateCap);

    private static AcceptanceCheckResult FailingCheck(
        string name,
        string identity,
        string outputTail,
        string? trxPath = null) =>
        new(
            name,
            false,
            1,
            outputTail,
            TestResultPaths: trxPath is null ? null : [trxPath],
            FailingTestIdentities: [identity],
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions:
            [
                new AcceptanceTestFailureAttribution(
                    identity,
                    AcceptanceTestFailureOrigin.Introduced,
                    "main is attested green for this identity")
            ]);

    private static AcceptanceVerificationSummary RedSummary(AcceptanceCheckResult unmet) =>
        new(
            false,
            [unmet],
            FailedChecks: [unmet.Name],
            BranchHeadSha: "candidate-a",
            MainHeadSha: "main-a",
            CheckAttributions:
            [
                new AcceptanceCheckAttribution(
                    unmet.Name,
                    AcceptanceFailureOrigin.Introduced,
                    "main is attested green")
            ]);

    private static void WriteTestSource(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var className = Path.GetFileNameWithoutExtension(relativePath);
        File.WriteAllText(
            fullPath,
            $"namespace Mcg.AgentOrchestrator.Infrastructure.Tests;{Environment.NewLine}" +
            $"public sealed class {className}{Environment.NewLine}{{{Environment.NewLine}}}{Environment.NewLine}");
    }

    private static void WriteSlotsBusyTrx(string trxPath, string identity)
    {
        var lastDot = identity.LastIndexOf('.');
        new XDocument(
            new XElement(
                "TestRun",
                new XElement(
                    "TestDefinitions",
                    new XElement(
                        "UnitTest",
                        new XAttribute("id", "slots-busy-1"),
                        new XAttribute("name", identity),
                        new XElement(
                            "TestMethod",
                            new XAttribute("className", identity[..lastDot]),
                            new XAttribute("name", identity[(lastDot + 1)..])))),
                new XElement(
                    "Results",
                    new XElement(
                        "UnitTestResult",
                        new XAttribute("testId", "slots-busy-1"),
                        new XAttribute("testName", identity),
                        new XAttribute("outcome", "Failed"),
                        new XElement(
                            "Output",
                            new XElement(
                                "ErrorInfo",
                                new XElement("Message", SlotsBusyMessage),
                                new XElement(
                                    "StackTrace",
                                    "Mcg.AgentOrchestrator.Infrastructure.DotnetBuildSlotsBusyException: " +
                                    SlotsBusyMessage))))))).Save(trxPath);
    }
}
