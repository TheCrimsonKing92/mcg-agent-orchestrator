using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class AcceptanceCohortWorkflowTestsLaneScopedAttribution : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void Reproducer_RunsOnlyFailedLaneWithEquivalentFullPlanVerdict()
    {
        using var scenario = new CohortLaneAttributionScenario([AttributionLaneFixture.Remainder], reproduceFirst: true);
        var result = scenario.Run();
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt);
        Assert.Equal(AcceptanceCohortAttributionOutcome.FirstMemberFailed, receipt.Attribution);
        var attributed = Assert.Single(receipt.AttributedMembers);
        Assert.Equal(scenario.First.Id, attributed.GoalId);
        Assert.Equal(new[] { scenario.Identity }, attributed.ReproducedFailingTests);
        Assert.Null(receipt.AttributionSource);
        Assert.All(scenario.Verifier.MemberChecks, checks => Assert.Equal(new[] { AttributionLaneFixture.Remainder }, checks));
        Assert.Equal(2, scenario.Verifier.MemberChecks.Count);
        Assert.Equal(3668, scenario.Verifier.LaneSeconds);
        Assert.Equal(14784, 2 * AttributionLaneFixture.Lanes.Sum(lane => lane.Seconds));
        Assert.Equal(receipt.Attribution, scenario.FullPlanVerdict(receipt));
        Assert.Equal(0, scenario.Verifier.FocusedCalls);
        Assert.Equal(2, scenario.LeaseRequests);
        AssertScopedReceipts(scenario, receipt);
        AssertNoSkip(scenario);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteractionOnly_RunsEveryCombinedFailedLaneIncludingMissingMemberLane(bool addProcess)
    {
        string[] failures = addProcess ? [AttributionLaneFixture.Remainder, AttributionLaneFixture.Process] : [AttributionLaneFixture.Remainder];
        using var scenario = new CohortLaneAttributionScenario(failures, omitSecondProcess: addProcess);
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Run().Receipt);
        Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
        Assert.Empty(receipt.AttributedMembers);
        Assert.Equal(2, scenario.Verifier.MemberChecks.Count);
        Assert.All(scenario.Verifier.MemberChecks, checks => Assert.Equal(failures, checks));
        if (addProcess)
        {
            Assert.DoesNotContain(AttributionLaneFixture.Process, scenario.Verifier.SecondPlanNames);
            Assert.Contains(AttributionLaneFixture.Process, scenario.Verifier.MemberChecks[1]);
        }
        Assert.Equal(receipt.Attribution, scenario.FullPlanVerdict(receipt));
        AssertScopedReceipts(scenario, receipt);
        AssertNoSkip(scenario);
    }

    [Fact]
    public void CliOnlyFailure_PreservesFullPlanReceiptAndManifestIdentity()
    {
        using var scenario = new CohortLaneAttributionScenario(["cli tests"]);
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Run().Receipt);
        Assert.Null(receipt.AttributionSource);
        Assert.Equal(2, scenario.Verifier.MemberChecks.Count);
        Assert.All(scenario.Verifier.Scopes, Assert.Null);
        Assert.All(scenario.Verifier.MemberChecks, checks => Assert.Equal(AttributionLaneFixture.Lanes.Select(lane => lane.Name), checks));
        Assert.All(scenario.Store.ReadPartitionReceipts(receipt.Identity.Value), partition =>
        {
            Assert.StartsWith("cohort-partition-v1-", partition.ReceiptId);
            Assert.Equal(CohortLaneAttributionScenario.ManifestIdentity, partition.ManifestIdentity);
            var payload = string.Join('\n', "cohort-partition-v1", partition.GoalId.Value, partition.CandidateRevision,
                partition.ObservedMainRevision, partition.TreeRevision ?? "unavailable", partition.ManifestIdentity);
            Assert.Equal("cohort-partition-v1-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload))), partition.ReceiptId);
        });
        AssertNoSkip(scenario);
    }

    private static void AssertScopedReceipts(CohortLaneAttributionScenario scenario, AcceptanceCohortReceipt receipt)
    {
        Assert.Null(receipt.AttributionSource);
        var partitions = scenario.Store.ReadPartitionReceipts(receipt.Identity.Value);
        Assert.Equal(2, partitions.Count);
        var scope = Assert.IsType<AcceptanceAttributionLaneScope>(scenario.Verifier.Scopes[0]);
        Assert.All(partitions, partition =>
        {
            Assert.StartsWith("cohort-lane-scoped-partition-v1-", partition.ReceiptId);
            Assert.Equal($"{CohortLaneAttributionScenario.ManifestIdentity};lane-scope-sha256={scope.Sha256}", partition.ManifestIdentity);
        });
    }

    private static void AssertNoSkip(CohortLaneAttributionScenario scenario) =>
        Assert.DoesNotContain(scenario.Events, record => record.Detail.Contains("ATTRIBUTION_SKIPPED", StringComparison.Ordinal));
}

internal sealed class CohortLaneAttributionScenario : AcceptanceCohortWorkflowTests, IDisposable
{
    internal const string ManifestIdentity = "fixture-full-plan-v1";
    internal string Root { get; }
    internal Goal First { get; }
    internal Goal Second { get; }
    internal ConductorDriver Driver { get; }
    internal OrchestratorWorkspace Workspace { get; }
    internal CohortAcceptanceStore Store { get; }
    internal RecordingVerifier Verifier { get; }
    internal ConductorAcceptanceCohortSelection Selection { get; }
    internal List<string> VerdictLines { get; } = [];
    internal int LeaseRequests { get; private set; }
    internal string Identity { get; }
    internal ConductEventRecord[] Events => GateProgressReportingVerifier.ReadEvents(Workspace.ConductEventsLogPath);

    internal CohortLaneAttributionScenario(string[] failedChecks, bool reproduceFirst = false,
        bool omitSecondProcess = false, bool noIdentities = false, bool selectableIdentity = false)
    {
        Root = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(Root);
            var kernel = new AgentOrchestratorKernel();
            First = CreateCompletedGoal(kernel, "First scoped attribution member", Root);
            Second = CreateCompletedGoal(kernel, "Second scoped attribution member", Root);
            CreateWorktreeCandidate(Root, First.Id, "src/Mcg.AgentOrchestrator.Infrastructure/First.cs", "first");
            CreateWorktreeCandidate(Root, Second.Id, "tests/Second.cs", "second");
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Identity = selectableIdentity ? "Tests.Lane.Fails" : "Tests.Lane.Case(x: 1)";
            Verifier = new RecordingVerifier(this, failedChecks, reproduceFirst, omitSecondProcess, noIdentities);
            Driver = new ConductorDriver(kernel, Workspace, Verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(Root).Hooks);
            Driver.CohortAttributionVerdictSink = VerdictLines.Add;
            Driver.CohortPartitionStableSlotLeaseSource = (_, _) =>
            {
                LeaseRequests++;
                return AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(Root);
            };
            Selection = ProjectSelection(Driver, First, Second);
            Store = new CohortAcceptanceStore(Path.Combine(Workspace.OrchestratorDirectory, "cohort-acceptance.db"));
        }
        catch { DeleteDirectory(Root); throw; }
    }

    internal ConductorAcceptanceCohortRunResult Run() =>
        Driver.RunAcceptanceCohort(Selection, [First, Second], ConductorAutonomyPolicy.Permissive);

    internal AcceptanceCohortAttributionOutcome FullPlanVerdict(AcceptanceCohortReceipt receipt)
    {
        var partitions = receipt.Identity.Members.Select((member, index) =>
        {
            var result = Verifier.MemberResult(index, AttributionLaneFixture.Plan);
            return new AcceptanceCohortPartitionReceipt($"full-{index}", member.GoalId, index, member.CandidateRevision,
                receipt.Identity.ObservedMainRevision, "full-tree", ManifestIdentity,
                result.Passed ? AcceptanceCohortGateOutcome.Passed : AcceptanceCohortGateOutcome.Failed, 0, result.TestResultPaths ?? [])
            {
                FailingTestIdentities = ConductorDriver.CohortFailingTestIdentities(result),
                FailedChecks = result.Checks!.Where(check => !check.Passed).Select(check => check.Name).ToArray()
            };
        }).ToArray();
        return ConductorAcceptanceCohortFailingTestAttribution.Classify([Identity], partitions[0], partitions[1]).Outcome;
    }

    public void Dispose() => DeleteDirectory(Root);

    internal sealed class RecordingVerifier(CohortLaneAttributionScenario scenario, string[] failedChecks,
        bool reproduceFirst, bool omitSecondProcess, bool noIdentities) : IGoalAcceptanceVerifier
    {
        internal List<GoalId?> GoalIds { get; } = [];
        internal List<string[]> MemberChecks { get; } = [];
        internal List<AcceptanceAttributionLaneScope?> Scopes { get; } = [];
        internal string[] SecondPlanNames { get; private set; } = [];
        internal int LaneSeconds { get; private set; }
        internal int FocusedCalls { get; private set; }
        public string ComputeEffectivePlanIdentity(string worktreePath, IReadOnlyList<string>? changedFiles = null) => ManifestIdentity;

        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex, DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            GoalIds.Add(goalId);
            if (goalId is null)
            {
                var path = WriteTrx("combined", !noIdentities);
                var checks = failedChecks.Select((name, index) => new AcceptanceCheckResult(name, false, 1, "combined failed",
                    FailingTestIdentities: !noIdentities && index == 0 ? [scenario.Identity] : [], TestResultPaths: [path])).ToArray();
                return Task.FromResult(new AcceptanceVerificationResult(false, false, 1, "combined failed", Checks: checks, TestResultPaths: [path]));
            }

            Assert.NotNull(stableSlotIndex);
            Assert.True(stableSlotLease!.IsExecutionLockHeld);
            var member = goalId == scenario.First.Id ? 0 : 1;
            var scope = ((AcceptanceAttemptExecutionOwner)executionOwner).AttributionLaneScope;
            Scopes.Add(scope);
            var plan = AttributionLaneFixture.Plan.Where(check => member == 0 || !omitSecondProcess || check.Name != AttributionLaneFixture.Process).ToArray();
            if (member == 1) SecondPlanNames = plan.Select(check => check.Name).ToArray();
            var applied = AcceptanceAttributionLaneScope.Apply(plan, AttributionLaneFixture.Plan, false, scope, TextWriter.Null);
            MemberChecks.Add(applied.Checks.Select(check => check.Name).ToArray());
            LaneSeconds += applied.Checks.Sum(check => AttributionLaneFixture.Lanes.Single(lane => lane.Name == check.Name).Seconds);
            return Task.FromResult(MemberResult(member, applied.Checks));
        }

        internal AcceptanceVerificationResult MemberResult(int member, IReadOnlyList<Check> checks)
        {
            var failed = member == 0 && reproduceFirst && checks.Any(check => check.Name == AttributionLaneFixture.Remainder);
            var path = WriteTrx($"member-{member}", failed);
            var results = checks.Select(check =>
            {
                var red = failed && check.Name == AttributionLaneFixture.Remainder;
                return new AcceptanceCheckResult(check.Name, !red, red ? 1 : 0, "fixture lane",
                    FailingTestIdentities: red ? [scenario.Identity] : [], TestResultPaths: [path]);
            }).ToArray();
            return new AcceptanceVerificationResult(!failed, false, failed ? 1 : 0, "fixture lanes", Checks: results, TestResultPaths: [path]);
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false)
        {
            FocusedCalls++;
            Assert.Equal($"FullyQualifiedName~{scenario.Identity}", request);
            Assert.True(stableSlotLease!.IsExecutionLockHeld);
            return Task.FromResult(new FocusedEvidenceRunResult(request, true, true, "green focused evidence",
                [new AcceptanceCheckResult("focused", true, 0, "green", ExecutedTestCount: 1,
                    TestResultPaths: [WriteTrx($"focused-{FocusedCalls}", false)])]));
        }

        private string WriteTrx(string label, bool failed)
        {
            var path = Path.Combine(scenario.Root, $"{label}.trx");
            var outcome = failed ? "Failed" : "Passed";
            File.WriteAllText(path, $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results><UnitTestResult testId="1" testName="{scenario.Identity}" outcome="{outcome}" /></Results>
                  <ResultSummary outcome="{(failed ? "Failed" : "Completed")}">
                    <Counters total="1" executed="1" passed="{(failed ? 0 : 1)}" failed="{(failed ? 1 : 0)}" />
                  </ResultSummary>
                </TestRun>
                """);
            return path;
        }
    }
}
