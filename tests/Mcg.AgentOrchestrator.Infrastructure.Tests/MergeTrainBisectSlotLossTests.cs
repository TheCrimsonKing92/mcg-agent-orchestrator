using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its repositories, database and build-slot storage root.
public sealed class MergeTrainBisectSlotLossTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedFirstGate_BusyBisectKeepsReceiptAndImplicatesBeforeLease(bool grouped)
    {
        WithTrain("fatal", scenario =>
        {
            IReadOnlySet<string>? beforeLease = null;
            scenario.Verifier.BeforeBisect = () =>
            {
                beforeLease = scenario.Driver.ReadTrainImplicatedMemberKeys();
                scenario.HoldSlots();
            };
            var mainBefore = RunGitOutput(scenario.Repo, "rev-parse", "main").Trim();
            MergeTrainReceipt receipt;
            if (grouped)
            {
                var attempt = Launch(scenario);
                ConductorGroupedGateOutcome? outcome = null;
                Assert.Equal(0, ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath, child =>
                    outcome = scenario.Driver.RunGroupedGateAttemptBody(child), (_, _) => new NoConsoleRedirection()));
                receipt = Assert.IsType<MergeTrainReceipt>(scenario.Store.TryReadReceipt(attempt.IdentityValue));
                Assert.NotNull(outcome);
                Assert.Equal("failed", outcome.Verdict);
                Assert.Equal(receipt.ReceiptId, outcome.ReceiptId);
                using var document = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
                var result = document.RootElement;
                Assert.Equal("completed", result.GetProperty("Status").GetString());
                Assert.Equal("failed", result.GetProperty("Verdict").GetString());
                Assert.Equal(receipt.ReceiptId, result.GetProperty("ReceiptId").GetString());
                Assert.False(result.TryGetProperty("Error", out _));
                Assert.False(result.TryGetProperty("ErrorType", out _));
                Assert.DoesNotContain("slots busy", File.ReadAllText(attempt.ResultPath), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var run = scenario.Driver.RunMergeTrain(scenario.Selection, scenario.Goals,
                    ConductorAutonomyPolicy.Permissive, gateOnly: true);
                receipt = Assert.IsType<MergeTrainReceipt>(run.RecordedReceipt);
                Assert.Null(run.Receipt);
                Assert.Empty(run.MemberResults);
                var ejection = Assert.Single(run.Ejections);
                Assert.Equal(scenario.Goals[0].Id, ejection.GoalId);
                Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
                Assert.Contains(nameof(DotnetBuildSlotsBusyException), run.Detail, StringComparison.Ordinal);
            }
            Assert.Equal(MergeTrainGateOutcome.Failed, receipt.Outcome);
            Assert.Equal(receipt.ReceiptId, scenario.Store.TryReadReceipt(receipt.Identity.Value)?.ReceiptId);
            Assert.Equal(scenario.Goals.Select(goal => goal.Id), receipt.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(new[] { "train" }, receipt.FailedChecks);
            Assert.Equal(1, receipt.GateExitCode);
            Assert.False(receipt.ValidForLanding);
            var failure = Assert.Single(MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths));
            Assert.Equal("Ns.OldestCulpritTests.Fails", failure.TestName);
            Assert.NotNull(beforeLease);
            Assert.Equal(scenario.Key(0), Assert.Single(beforeLease));
            Assert.Equal(scenario.Key(0), Assert.Single(scenario.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.Null(ConductorMergeTrainSelector.Select(scenario.Selection.Members.Select(member =>
                new ConductorSpeculativeAcceptanceCandidate(member.GoalId,
                    new GateReadyCandidateProjectionResult.Ready(member))).ToArray(),
                trainImplicatedMemberKeys: scenario.Driver.ReadTrainImplicatedMemberKeys()));
            Assert.Equal(1, scenario.Verifier.Inner.RunCount);
            Assert.Equal(Paths, Assert.Single(scenario.Verifier.Inner.ChangedFiles));
            Assert.Equal(mainBefore, RunGitOutput(scenario.Repo, "rev-parse", "main").Trim());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyFirstLease_WithoutReceiptStillThrowsAndHostRecordsError(bool grouped)
    {
        WithTrain("fatal", scenario =>
        {
            scenario.HoldSlots();
            if (grouped)
            {
                var attempt = Launch(scenario);
                Assert.Equal(1, ConductorGroupedGateAttemptHost.Run(attempt.MetadataPath,
                    scenario.Driver.RunGroupedGateAttemptBody, (_, _) => new NoConsoleRedirection()));
                using var document = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
                var result = document.RootElement;
                Assert.Equal("failed", result.GetProperty("Status").GetString());
                Assert.Equal(nameof(DotnetBuildSlotsBusyException), result.GetProperty("ErrorType").GetString());
                Assert.Contains("slots busy", result.GetProperty("Error").GetString()!, StringComparison.OrdinalIgnoreCase);
                Assert.False(result.TryGetProperty("ReceiptId", out _));
            }
            else
                Assert.Throws<DotnetBuildSlotsBusyException>(() => scenario.Driver.RunMergeTrain(
                    scenario.Selection, scenario.Goals, ConductorAutonomyPolicy.Permissive, gateOnly: true));
            Assert.Equal(0, scenario.Verifier.Inner.RunCount);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            using var connection = new SqliteConnection($"Data Source={scenario.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM merge_train_receipts;";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        });
    }

    [Theory]
    [InlineData("apparatus")]
    [InlineData("unreadable")]
    [InlineData("unattributed")]
    public void FirstGateWithoutAttributedGenuineRed_RecordsNoImplication(string evidence)
    {
        WithTrain(evidence, scenario =>
        {
            IReadOnlySet<string>? beforeLease = null;
            scenario.Verifier.BeforeBisect = () =>
            {
                beforeLease = scenario.Driver.ReadTrainImplicatedMemberKeys();
            };
            var run = scenario.Driver.RunMergeTrain(scenario.Selection, scenario.Goals,
                ConductorAutonomyPolicy.Permissive, gateOnly: true);
            Assert.Empty(scenario.Driver.ReadTrainImplicatedMemberKeys());
            Assert.Empty(run.MemberResults);
            if (evidence == "unreadable")
            {
                var receipt = Assert.IsType<MergeTrainReceipt>(run.RecordedReceipt);
                Assert.Equal(1, scenario.Verifier.Inner.RunCount);
                Assert.Null(run.Receipt);
                Assert.Equal(MergeTrainGateOutcome.InfrastructureFailure, receipt.Outcome);
                Assert.Null(beforeLease);
                Assert.Empty(run.Ejections);
                // A Failed receipt with unreadable evidence cannot arise through classification.
                Assert.False(MergeTrainBisectRedRetention.RecordsBeforeBisect(
                    receipt with { Outcome = MergeTrainGateOutcome.Failed }, 3, 0, scenario.Selection.BindMembers()[0]));
                Assert.False(MergeTrainBisectRedRetention.KeepsFailedReceipt(receipt));
            }
            else
            {
                Assert.Equal(2, scenario.Verifier.Inner.RunCount);
                Assert.Equal(MergeTrainGateOutcome.Passed, Assert.IsType<MergeTrainReceipt>(run.Receipt).Outcome);
                Assert.NotNull(beforeLease);
                Assert.Empty(beforeLease);
                Assert.Equal(MergeTrainEjectionReason.RedNewestMember, Assert.Single(run.Ejections).Reason);
            }
            Assert.False(MergeTrainBisectRedRetention.KeepsFailedReceipt(null));
        });
    }

    private static readonly string[] Paths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/OldestCulpritTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherMemberTests.cs",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestMemberTests.cs"
    ];

    private void WithTrain(string evidence, Action<Scenario> assert)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var leases = new List<DotnetBuildEnvironmentLease>();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var names = new[] { "OldestCulpritTests", "OtherMemberTests", "NewestMemberTests" };
            var goals = names.Select((name, index) =>
            {
                var goal = CreateCompletedGoal(kernel, $"Train member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                    { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                _ = CreateWorktreeCandidate(repo, goal.Id, Paths[index], $"namespace Ns; public class {name} {{ }}");
                return goal;
            }).ToArray();
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var cleanup = CreateIsolatedCleanupContext(repo).Hooks;
            var verifier = new BisectSlotVerifier(new SequenceAcceptanceVerifier([Red(repo, evidence),
                new(true, false, 0, null, TestResultPaths: [WritePassingTrx(repo, "remainder.trx")])]));
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(workspace.OrchestratorDirectory, "parallel-attempts"), workspace.IntegrationBranch,
                buildStorageRoot: cleanup.BuildStorageRoot);
            var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), coordinator, cleanup);
            var selection = ProjectTrainSelection(driver, goals);
            var databasePath = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db");
            assert(new Scenario(repo, goals, driver, selection, verifier, databasePath, () =>
            {
                Assert.Empty(leases);
                for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
                    leases.Add(DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.Zero, storageRoot: cleanup.BuildStorageRoot, holderLabel: "bisect-slot-loss-fixture"));
                Assert.All(leases, lease => Assert.True(lease.IsExecutionLockHeld));
                Assert.Equal(DotnetBuildEnvironmentManager.StableSlotCount,
                    leases.Select(lease => lease.Environment.ExecutionLockPath).Distinct().Count());
                Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                    DotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                        TimeSpan.Zero, storageRoot: cleanup.BuildStorageRoot));
            }));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            DeleteDirectory(repo);
        }
    }

    private static ConductorGroupedGateAttempt Launch(Scenario scenario)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(scenario.Repo);
        var launches = new List<ConductorGroupedGateAttempt>();
        scenario.Driver.EnableOwnedGroupedGateAttempts(new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"),
            launch: attempt =>
            {
                launches.Add(attempt);
                return new ConductorGroupedGateLaunchResult(Environment.ProcessId, null, null);
            }, isProcessAlive: _ => true));
        var run = scenario.Driver.RunMergeTrain(scenario.Selection, scenario.Goals,
            ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
        Assert.Contains("outcome=inflight", run.Detail, StringComparison.Ordinal);
        Assert.Null(run.Receipt);
        return Assert.Single(launches);
    }

    private static AcceptanceVerificationResult Red(string repo, string evidence)
    {
        var path = Path.Combine(repo, "first-gate.trx");
        var identity = evidence == "unattributed" ? "Ns.UnchangedTests.Fails" : "Ns.OldestCulpritTests.Fails";
        var message = evidence == "apparatus"
            ? "DotnetBuildSlotsBusyException: Stable dotnet build slots busy" : "Assert.Equal failed";
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "Results", new XElement(ns + "UnitTestResult",
                new XAttribute("testId", "1"), new XAttribute("testName", identity), new XAttribute("outcome", "Failed"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", message))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(ns + "Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1),
                new XAttribute("passed", 0), new XAttribute("failed", 1))))).Save(path);
        if (evidence == "unreadable") File.WriteAllText(path, "<TestRun");
        else
        {
            var read = AcceptanceTrxFailureReader.Read(path);
            Assert.Equal(AcceptanceTrxReadStatus.Readable, read.Status);
            var failure = Assert.Single(read.Failures);
            Assert.True(AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome));
            Assert.Equal(evidence == "apparatus",
                ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is not null);
        }
        return new AcceptanceVerificationResult(false, false, 1, "train failed",
            Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [path]);
    }

    private sealed record Scenario(string Repo, Goal[] Goals, ConductorDriver Driver,
        ConductorMergeTrainSelection Selection, BisectSlotVerifier Verifier, string DatabasePath, Action HoldSlots)
    {
        internal MergeTrainAcceptanceStore Store => new(DatabasePath);
        internal string Key(int member) => ConductorAcceptanceCohortAttributedMembers.Key(
            Selection.Members[member].GoalId, Selection.Members[member].CandidateRevision);
    }

    private sealed class BisectSlotVerifier(SequenceAcceptanceVerifier inner) : IGoalAcceptanceVerifier
    {
        internal SequenceAcceptanceVerifier Inner => inner;
        internal Action? BeforeBisect { get; set; }
        private bool _bisectObserved;

        public string ComputeEffectivePlanIdentity(string worktreePath, IReadOnlyList<string>? changedFiles = null)
        {
            if (inner.RunCount == 1 && !_bisectObserved)
            {
                _bisectObserved = true;
                BeforeBisect?.Invoke();
            }
            return ((IGoalAcceptanceVerifier)inner).ComputeEffectivePlanIdentity(worktreePath, changedFiles);
        }

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) =>
            inner.RunOwnedAsync(worktreePath, goalId, changedFiles, stableSlotIndex, stableSlotLease, executionOwner);

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) => throw new NotSupportedException();
    }

    private sealed class NoConsoleRedirection : IDisposable
    {
        public void Dispose() { }
    }
}
