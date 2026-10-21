using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

public sealed class ConductorDriverTestsAttributionPending
{
    // Historical excerpt: result 68C785CC..., first TRX DA3F8A0B..., rerun CE6BA2E1....
    internal const string TimeoutMessage = "Child 'C:\\TestFixture\\AppData\\Local\\Programs\\PowerShell\\7\\pwsh.exe' -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\\TestFixture\\AppData\\Local\\Temp\\mcg-hvp\\AppData\\Local\\Temp\\mcg-bridge-641060c312a14d8f98bdc3ccb80a926c\\repo\\repair.ps1 exceeded 120 s while capturing stdout/stderr or exit; stdout=0 bytes; stderr=0 bytes; retention limit=4194304 bytes.";
    internal const string BaselineUnavailable = "focused baseline execution failed: DotnetBuildSlotsBusyException";

    [Fact]
    public void ProductionMaterializationKeepsExecutedRedSeparateFromBaselineAttribution()
    {
        using var fixture = new Fixture();
        Assert.Null(ApparatusInfrastructureSignatures.Match(TimeoutMessage, "at TestChildProcessCapture.Fail(String stream, TimeSpan bound)"));
        Assert.Equal("dotnet-build-slots-busy", ApparatusInfrastructureSignatures.Match(BaselineUnavailable));
        Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, (AcceptanceTestFailureOrigin)2);
        var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        Assert.Equal(3, reading.FailingTests.Count);
        Assert.Equal(2, reading.FailingTests.Count(test => test.CandidateRerunFailed));
        Assert.False(reading.FailingTests.Single(test => test.TestIdentity == Fixture.Identities[2]).CandidateRerunFailed);
        var pending = Assert.IsType<ApparatusRedDisposition.AttributionPending>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.Equal(Fixture.Identities.Take(2).Order(StringComparer.Ordinal), pending.PendingTests.Select(test => test.TestIdentity));
        Assert.All(pending.PendingTests, test =>
        {
            Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, test.BaselineAttribution!.Origin);
            Assert.Equal(BaselineUnavailable, test.BaselineAttribution.Evidence);
            Assert.Equal(fixture.Check.LaneRerun!.Attribution, test.ConfirmedLaneFailure!.Attribution);
            Assert.Null(test.ExceptionSignature);
        });
        Assert.Equal(3, fixture.Check.LaneRerun!.FirstFailingTestIdentities.Count);
        Assert.All(fixture.Check.LaneRerun.FirstTestResultPaths, path => Assert.True(File.Exists(path)));
        // A label without producer-bound custody cannot activate this inference. This is also the
        // pre-change route expected by Acceptance's handoff-only negative control.
        fixture.Check = fixture.Check with { LaneRerun = fixture.Check.LaneRerun with { Attribution = null } };
        var unbound = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        Assert.All(unbound.FailingTests, test => Assert.False(test.CandidateRerunFailed));
        Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(fixture.Goal, unbound));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(0, true)]
    public async Task ConfirmedRedHoldsWithoutAdmissionRetryOrDuplicateAttention(int cap, bool reverse)
    {
        using var fixture = new Fixture(cap);
        // The native final result contains the two remaining failures. The passing third identity
        // and both extra passing cases remain in the original first/rerun receipts.
        fixture.Check = fixture.Check with { FailingTestIdentities = (reverse
            ? Fixture.Identities.Take(2).Reverse() : Fixture.Identities.Take(2)).ToArray() };
        Assert.True(fixture.Kernel.BeginGoalAcceptanceVerification(fixture.Goal.Id, "native attempt"));
        Assert.True(fixture.Kernel.ReconcileGoalAcceptanceFailed(fixture.Goal.Id, [fixture.Check.Name], "native RED", Fixture.Candidate, Fixture.Main));
        var occurrence = fixture.Goal.LatestAcceptanceFailure!.OccurredAt;
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var gates = 0;
        var retries = 0;
        var driver = MakeDriver(runAcceptanceSummary: _ => { gates++; return fixture.Summary; },
            retryTaskWithCause: (_, _, _, _, _) => { retries++; throw new InvalidOperationException("Unexpected Developer reopen"); },
            recordCriterionRetryFeedback: fixture.Kernel.RecordCriterionRetryFeedback,
            recordAcceptanceFailure: (goal, checks, branch, main, attrs, baseline) => fixture.Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attrs, baseline),
            getLandingFileScopes: _ => ["src/Changed.cs"], apparatusRedGate: fixture.Gate,
            writeEscalationWithResult: (goal, _, reason) => OperatorInbox.RecordLandingEscalation(workspace, goal, reason, "conductor:acceptance", collaborationStore: store));
        // The native terminal result already exists: no automatic gate admission is needed to route it.
        var candidate = ConductorParallelAcceptanceCandidate.Create(fixture.Goal, 0, ["src/Changed.cs"], Fixture.Candidate, Fixture.Main);
        var first = driver.CompleteParallelLandingAcceptance(candidate, ConductorAutonomyPolicy.Conservative, fixture.Summary, out var leaseHeld);
        Assert.False(leaseHeld);
        var held = AssertPending(first, fixture.Goal);
        Assert.Equal(occurrence, fixture.Goal.LatestAcceptanceFailure!.OccurredAt);
        var receiptFacts = held.Decision!.Facts.Single(fact => fact.Name == "receiptReferences").Value;
        foreach (var receipt in fixture.Check.LaneRerun!.Attribution!.FirstReceipts.Concat(fixture.Check.LaneRerun.Attribution.RerunReceipts))
        {
            Assert.Contains(receipt.Path, receiptFacts, StringComparison.Ordinal);
            Assert.Contains(receipt.Sha256, receiptFacts, StringComparison.Ordinal);
        }
        Assert.Contains(Fixture.Attempt, held.Reason, StringComparison.Ordinal);
        Assert.Contains(BaselineUnavailable, held.Reason, StringComparison.Ordinal);
        Assert.Contains($"acceptance-retry {fixture.Goal.Id.Value[..8]} <reason> --confirm-acceptance-retry", held.Reason, StringComparison.Ordinal);
        for (var i = 0; i < 2; i++)
            Assert.Equal(held.StableIdentity, AssertPending(driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative), fixture.Goal).StableIdentity);
        // Restart through the actual persisted goal snapshot, with no receipt directory scan.
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        var restored = restoredKernel.GetGoal(fixture.Goal.Id);
        Assert.Equal(held.Reason, AssertPending(driver.AdvanceOnce(restored, ConductorAutonomyPolicy.Conservative), restored).Reason);
        var attention = Assert.Single(await store.ListAsync(fixture.Goal.Id.Value));
        Assert.Equal(CollaborationItemStatus.Raised, attention.Status);
        Assert.Contains(Fixture.Attempt, attention.Body, StringComparison.Ordinal);
        Assert.All(Fixture.Identities.Take(2), identity => Assert.Contains(identity, attention.Body, StringComparison.Ordinal));
        Assert.Equal(0, gates);
        Assert.Equal(0, retries);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, fixture.Goal.Tasks.Single().CriterionRetryCount);
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
        Assert.Equal(WorkTaskStatus.Completed, fixture.Goal.Tasks.Single().Status);
    }

    internal static ConductorAdvanceOutcome.Held AssertPending(ConductorAdvanceResult result, Goal goal)
    {
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Equal(GoalLifecycleState.AcceptanceFailed, held.State);
        Assert.NotNull(goal.LatestAcceptanceFailure!.AttributionPending);
        var facts = AcceptanceApparatusDispositionFacts.FromRecordedFacts(held.Decision!.Facts);
        Assert.Equal(AcceptanceApparatusDisposition.AttributionPending, facts.Disposition);
        Assert.Equal(AcceptanceAttributionPending.EvidenceKind, held.Decision.DiscriminatingEvidence);
        Assert.Equal("Hold", held.Decision.Action);
        Assert.Equal(held.Reason, facts.Reason);
        Assert.Null(facts.RegateOrdinal);
        Assert.Null(facts.RegateCount);
        Assert.Null(facts.RegateCap);
        return held;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenuineReasonsWinOverPendingInEitherOrder(bool reverse)
    {
        using var fixture = new Fixture();
        fixture.Check = fixture.Check with { FailingTestAttributions = fixture.Check.FailingTestAttributions!.Select((attribution, i) =>
            i == 0 ? attribution with { Origin = AcceptanceTestFailureOrigin.Introduced, Evidence = "identity passed executed baseline" } : attribution).ToArray(),
            FailingTestIdentities = reverse ? Fixture.Identities.Reverse().ToArray() : Fixture.Identities };
        var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        Assert.Contains(Fixture.Identities[0], Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, reading)).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedIdentityDoesNotMaskAnotherPendingIdentity(bool reverse)
    {
        using var fixture = new Fixture();
        fixture.Check = fixture.Check with { FailingTestAttributions = fixture.Check.FailingTestAttributions!.Select((attribution, i) =>
            i == 0 ? attribution with { Origin = AcceptanceTestFailureOrigin.Inherited, Evidence = "executed baseline failed for this identity" } : attribution).ToArray(),
            FailingTestIdentities = reverse ? Fixture.Identities.Reverse().ToArray() : Fixture.Identities };
        var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        var pending = Assert.IsType<ApparatusRedDisposition.AttributionPending>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.Equal(Fixture.Identities[1], Assert.Single(pending.PendingTests).TestIdentity);
        Assert.Equal(ApparatusRedClassifier.CrossGoalEvidenceKind, pending.OtherEvidenceKind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InheritedWithoutHistory_WithPendingOrInfrastructureSibling_RemainsGenuine(
        bool infrastructureSibling, bool reverse)
    {
        // All files and history are scoped to this fixture; no process or ambient state is used.
        using var fixture = new Fixture(firstIdentityHasHistory: false);
        fixture.Check = fixture.Check with
        {
            FailingTestAttributions = fixture.Check.FailingTestAttributions!.Select((attribution, i) =>
                i == 0 ? attribution with { Origin = AcceptanceTestFailureOrigin.Inherited,
                    Evidence = "executed baseline failed for this identity" } : attribution).ToArray(),
            FailingTestIdentities = (reverse
                ? Fixture.Identities.Take(2).Reverse() : Fixture.Identities.Take(2)).ToArray()
        };
        if (infrastructureSibling)
        {
            foreach (var path in fixture.Check.LaneRerun!.FirstTestResultPaths.Concat(fixture.Check.LaneRerun.RerunTestResultPaths))
            {
                var trx = XDocument.Load(path);
                var sibling = trx.Descendants("UnitTestResult").Single(row =>
                    (string?)row.Attribute("testName") == Fixture.Identities[1]);
                sibling.Descendants("Message").Single().Value = "Stable dotnet build slots busy";
                trx.Save(path);
            }
            fixture.Check = AcceptanceLaneRerunAttributionReader.Attach(
                fixture.Check with { LaneRerun = fixture.Check.LaneRerun with { Attribution = null } },
                fixture.Goal.Id.Value, Fixture.Candidate, Fixture.Main);
        }

        Assert.DoesNotContain(fixture.Index.Read(), record => record.TestIdentity == Fixture.Identities[0]);
        var reading = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        Assert.All(reading.FailingTests, test =>
        {
            Assert.True(test.CandidateRerunFailed);
            Assert.NotNull(test.ConfirmedLaneFailure);
        });
        var inherited = reading.FailingTests.Single(test => test.TestIdentity == Fixture.Identities[0]);
        Assert.Equal(AcceptanceTestFailureOrigin.Inherited, inherited.BaselineAttribution!.Origin);
        Assert.Null(inherited.ExceptionSignature);
        var other = reading.FailingTests.Single(test => test.TestIdentity == Fixture.Identities[1]);
        Assert.Equal(infrastructureSibling ? "dotnet-build-slots-busy" : null, other.ExceptionSignature);
        Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, other.BaselineAttribution!.Origin);
        var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.Equal($"failing test {Fixture.Identities[0]} has neither a recorded infrastructure signature " +
            "nor a prior cross-goal occurrence", genuine.Reason);
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
    }

    [Fact]
    public void ChangedTestStillRoutesAsGenuineAndCandidateInfrastructureKeepsItsPrecedence()
    {
        using var fixture = new Fixture();
        var changed = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary,
            new(() => ["tests/Sample.Tests/" + Fixture.ClassName + ".cs"]))!;
        Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, changed));
        foreach (var path in fixture.Check.LaneRerun!.FirstTestResultPaths.Concat(fixture.Check.LaneRerun.RerunTestResultPaths))
            File.WriteAllText(path, File.ReadAllText(path).Replace(TimeoutMessage, "Stable dotnet build slots busy", StringComparison.Ordinal));
        fixture.Check = AcceptanceLaneRerunAttributionReader.Attach(
            fixture.Check with { LaneRerun = fixture.Check.LaneRerun with { Attribution = null } },
            fixture.Goal.Id.Value, Fixture.Candidate, Fixture.Main);
        var infrastructure = fixture.Gate.RecordGateCompletion(fixture.Goal, fixture.Summary, new(() => ["src/Changed.cs"]))!;
        Assert.All(infrastructure.FailingTests, test => Assert.Equal("dotnet-build-slots-busy", test.ExceptionSignature));
        Assert.Equal(ApparatusInfrastructureSignatures.EvidenceKind,
            Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(fixture.Goal, infrastructure)).EvidenceKind);
    }

    internal sealed class Fixture : IDisposable
    {
        internal const string Candidate = "1e5cd870aa0bab881ab4567ec0a9b0709b16fca7";
        internal const string Main = "d9ae043b5e51fedbf9234917f792dfbc71c93990";
        internal const string Attempt = "bound-failed-attempt";
        internal const string CheckName = "infrastructure tests process spawning";
        internal const string ClassName = "MtpTestRunnerScriptTestsWorkerBuildNoBuildBridge";
        internal static readonly string[] Identities = [
            ClassName + ".RealHelpersReuseWorkerBuildWithoutCompilation(fallbackSlot: True)",
            ClassName + ".RealHelpersReuseWorkerBuildWithoutCompilation(fallbackSlot: False)",
            ClassName + ".SnapshotRejectsIdentityChangedAfterSelection(mutation: \"source\", reason: \"Selected worker build identity changed while copyi\"···)"];
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal AcceptanceFailingTestIndex Index { get; }
        internal ApparatusRedGate Gate { get; }
        internal AcceptanceCheckResult Check { get; set; }
        internal AcceptanceVerificationSummary Summary => new(false, [Check], FailedChecks: [Check.Name],
            BranchHeadSha: Candidate, MainHeadSha: Main, AttemptId: Attempt);

        internal Fixture(int cap = 2, bool firstIdentityHasHistory = true)
        {
            (Kernel, Goal) = SimpleGoal();
            PassVerification(Kernel, Goal, Goal.Tasks.Single());
            var source = Path.Combine(Root, "tests", "Sample.Tests", ClassName + ".cs");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, $"public sealed class {ClassName} {{ }}");
            var first = WriteTrx("first.trx", Identities.Select(identity => (identity, "Failed")));
            var rerun = WriteTrx("rerun.trx", Identities.Select((identity, i) => (identity, i < 2 ? "Failed" : "Passed"))
                .Concat([(ClassName + ".SnapshotRejectsIdentityChangedAfterSelection(mutation: \"closure\")", "Passed"),
                         (ClassName + ".SnapshotRejectsIdentityChangedAfterSelection(mutation: \"receipt\")", "Passed")]));
            Check = new(CheckName, false, 1, TimeoutMessage, TestResultPaths: [first, rerun],
                TestResultAttemptId: Attempt, TestResultRunOrdinal: 1, FailingTestIdentities: Identities,
                ExecutedTestCount: 5, CompletionDecision: new(false, "failing-trx", false, 1, 5, 5, "Failed"),
                TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
                FailingTestAttributions: Identities.Take(2).Select(identity => new AcceptanceTestFailureAttribution(
                    identity, AcceptanceTestFailureOrigin.Unattributed, BaselineUnavailable)).ToArray())
            {
                LaneRerun = new($"{Attempt}:infrastructure-tests-process-spawning:0", [first], Identities, "failing-trx",
                    $"{Attempt}:infrastructure-tests-process-spawning:1", [rerun], Identities.Take(2).ToArray(), "failing-trx", "confirmed-failure")
            };
            Check = AcceptanceLaneRerunAttributionReader.Attach(Check, Goal.Id.Value, Candidate, Main);
            var now = DateTimeOffset.Parse("2026-10-20T09:45:00Z", null);
            Index = new(Path.Combine(Root, "failing-index.jsonl"));
            Index.Append(Identities.Where((_, i) => firstIdentityHasHistory || i != 0)
                .Select(identity => new AcceptanceFailingTestIndexRecord(
                AcceptanceFailingTestIndex.ContractVersion, AcceptanceFailingTestIndexKinds.GateFailure,
                "other-goal", now.AddHours(-1), CheckName: CheckName, TestIdentity: identity,
                ResolvedSourcePath: "tests/Sample.Tests/" + ClassName + ".cs", InsideChangedPaths: false,
                MessageFingerprint: AcceptanceFailingTestIndex.ComputeMessageFingerprint(TimeoutMessage))).ToArray(), now);
            Gate = new(Index, _ => Root, () => now, perGoalRegateCap: cap);
        }

        internal string WriteTrx(string name, IEnumerable<(string Identity, string Outcome)> rows)
        {
            var path = Path.Combine(Root, name);
            new XDocument(new XElement("TestRun", new XAttribute("id", Guid.NewGuid()),
                new XElement("Results", rows.Select(row => new XElement("UnitTestResult",
                    new XAttribute("testName", row.Identity), new XAttribute("outcome", row.Outcome),
                    new XElement("Output", new XElement("ErrorInfo", new XElement("Message", TimeoutMessage)))))))).Save(path);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
