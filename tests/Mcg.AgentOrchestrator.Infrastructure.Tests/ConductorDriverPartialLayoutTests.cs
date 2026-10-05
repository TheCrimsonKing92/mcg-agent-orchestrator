using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorDriverPartialLayoutTests
{
    [Fact]
    public void ParallelAcceptanceMembersHaveOneOwningFile() =>
        AssertMemberLocations("ConductorDriver.ParallelAcceptance.cs",
            ("VerifyingFindingTrigger", 1),
            ("TryBuildParallelAcceptanceCandidate", 1),
            ("ProjectGateReadyCandidate", 1),
            ("ExcludedGateReadyCandidate", 1),
            ("RecoverCohortLandingEffects", 1),
            ("ReadSuppressedCohortPairs", 1),
            ("ResetCohortFairness", 1),
            ("TryGetCohortGateHold", 1),
            ("RecordMergeTrainAdmissionFairness", 1),
            ("RunPreReviewFocusedEvidence", 1),
            ("RunParallelLandingAcceptancePreSlot", 1),
            ("RefreshParallelAcceptanceCandidate", 1),
            ("IsAcceptanceAttemptCancelled", 1),
            ("GetAcceptanceAttemptCancellationDecision", 1),
            ("CompleteParallelLandingAcceptance", 1),
            ("EscalateParallelLandingAcceptance", 2),
            ("NormalizeNamedFailedChecksForRetry", 1),
            ("ClassifyInheritedBaselineApparatus", 1),
            ("IsEnvironmentalApparatusAcceptanceRun", 1),
            ("IsSameApparatusFailurePair", 1),
            ("HasActiveApparatusHold", 1),
            ("ShaIsUnchangedOrUnknown", 1),
            ("ReplayParallelLandingEarlyOutcome", 1),
            ("ReplayMissingBranchRetirement", 1));

    [Fact]
    public void DispatchDiagnosticsMembersHaveOneOwningFile() =>
        AssertMemberLocations("ConductorDriver.DispatchDiagnostics.cs",
            ("GetCurrentGoal", 1),
            ("TryRecoverSandboxPrep", 1),
            ("EmitPhaseTiming", 2),
            ("EmitGoalPhaseTiming", 1),
            ("RunDispatchRemediation", 1),
            ("RunBoundedBuildServerShutdown", 1),
            ("RunRetryOnlyRemediation", 1),
            ("AppendGateProgressEvent", 1),
            ("AppendCohortGateProgressEvents", 1),
            ("FormatGateProgressConductEvent", 1),
            ("FormatConductToken", 1),
            ("CountAssignedTasks", 1),
            ("HasAssignedDeveloperReadyForDispatch", 1),
            ("FormatPreparedDispatchWithoutStart", 1),
            ("FormatSourceCleanupPaths", 1),
            ("DescribeEmptyBatch", 1),
            ("FormatReadyBlockedDiagnostic", 1),
            ("FormatAssignedTasksBlockedReason", 1),
            ("FormatAssignedTaskBlocker", 1),
            ("TryDescribeCancelledPredecessorBlocker", 1),
            ("ComputeEmptyOutputBackoff", 1),
            ("TryGetDispatchRecoveryAction", 1),
            ("GetDispatchRecoveryAction", 1),
            ("IsRetryableStaleRecovery", 1),
            ("ExtractDispatchRecoveryDiagnostic", 1),
            ("FormatFailureTail", 1),
            ("IsBlockingTimeoutCheck", 1));

    [Fact]
    public void GitAndLeaseHelpersMembersHaveOneOwningFile() =>
        AssertMemberLocations("ConductorDriver.GitAndLeaseHelpers.cs",
            ("TryResolveGitHead", 1),
            ("ReadLandingRecheckEvidence", 1),
            ("TryResolveAcceptanceBranchHead", 1),
            ("FormatAcceptanceCandidate", 1),
            ("IsCommitReachableFromMain", 1),
            ("RecoverMainMergeCommitForBranchTip", 1),
            ("FormatShortSha", 1),
            ("FormatSlotsBusy", 1),
            ("FormatBuildLockBlocked", 1),
            ("TryGetActiveEvidenceMutationLease", 1),
            ("FormatEvidenceMutationLeaseHeld", 1),
            ("ReplacementEvidenceMutationHeld", 1),
            ("NoopEvidenceMutationLease", 1));

    [Fact]
    public void PreReviewEvidenceRoutingMembersHaveOneOwningFile() =>
        AssertMemberLocations("ConductorDriver.PreReviewEvidenceRouting.cs",
            ("TryStopRepeatedPreReviewMappingRetry", 1),
            ("TryRoutePreReviewEvidenceToDeveloper", 1),
            ("TryRoutePreReviewEvidenceToTester", 1),
            ("BuildPreReviewEvidenceContext", 3),
            ("GetPreReviewEvidenceContext", 1),
            ("BuildAddTesterCommand", 1),
            ("TasksBefore", 1),
            ("GetCurrentReviewerRoundNumber", 1),
            ("ExtractFailingTestIdentities", 1),
            ("BuildPreReviewEvidencePointer", 1));

    private static void AssertMemberLocations(
        string owningFile,
        params (string Name, int Count)[] expectedMembers)
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var folder = Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "Orchestration");
        var target = Path.Combine(folder, owningFile);
        Assert.True(File.Exists(target), $"Expected conductor partial: {owningFile}");
        var files = Directory.GetFiles(folder, "ConductorDriver*.cs")
            .ToDictionary(Path.GetFileName, File.ReadAllLines);

        foreach (var member in expectedMembers)
        {
            // Anchor at class-member indentation and modifiers to exclude calls and nameof references.
            var declaration = new Regex(
                @"^    (?:private|internal|public|protected)\b[^=;]*?[\s)>?\]]" +
                Regex.Escape(member.Name) + @"\s*(\(|:|$)");
            foreach (var file in files)
            {
                var expected = file.Key == owningFile ? member.Count : 0;
                var actual = file.Value.Count(line => declaration.IsMatch(line));
                Assert.True(actual == expected,
                    $"{member.Name} declarations in {file.Key}: expected {expected}, actual {actual}");
            }
        }
    }
}
