namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceStructuralCoverageBaseline(
    string[] DiscoveryArguments,
    string WorktreePath,
    string RepositoryRoot,
    bool BareTestList,
    bool LockRemediationApplied);

internal sealed record AcceptanceStructuralCoverageRequest(
    string[] CandidateDiscoveryArguments,
    string CandidateWorktreePath,
    TimeSpan DiscoveryTimeout,
    bool BareTestList,
    Func<IReadOnlyList<TestPartitionCoverage>> ResolvePartitions,
    Func<IReadOnlyList<string>> ResolveDeletedTestFiles,
    string? CurrentAttemptId,
    IReadOnlyList<string> SanctionedRemovedTests,
    Func<CancellationToken, Task<AcceptanceStructuralCoverageBaseline?>> PrepareBaseline,
    Func<CancellationToken, Task<AcceptanceContainedGenerationBaseline>>? PrepareContainedBaseline = null);

internal sealed record AcceptanceStructuralCoverageEvaluation(
    GoalAcceptanceVerifier.CommandResult CandidateDiscovery,
    GoalAcceptanceVerifier.CommandResult? BaselineDiscovery,
    Exception? BaselineDiscoveryIoException,
    TestCoverageInvariantResult? Coverage,
    bool BaselineLockRemediationApplied);

internal sealed class AcceptanceStructuralCoverageEvaluator
{
    private readonly Func<
        string[],
        string,
        TimeSpan,
        CancellationToken,
        Task<GoalAcceptanceVerifier.CommandResult>> _discoveryRunner;
    private readonly Func<Exception, bool> _isBaselineDiscoveryIoException;

    internal AcceptanceStructuralCoverageEvaluator(
        Func<
            string[],
            string,
            TimeSpan,
            CancellationToken,
            Task<GoalAcceptanceVerifier.CommandResult>> discoveryRunner)
        : this(discoveryRunner, _ => false)
    {
    }

    internal AcceptanceStructuralCoverageEvaluator(
        Func<
            string[],
            string,
            TimeSpan,
            CancellationToken,
            Task<GoalAcceptanceVerifier.CommandResult>> discoveryRunner,
        Func<Exception, bool> isBaselineDiscoveryIoException)
    {
        _discoveryRunner = discoveryRunner;
        _isBaselineDiscoveryIoException = isBaselineDiscoveryIoException;
    }

    internal async Task<AcceptanceStructuralCoverageEvaluation> EvaluateAsync(
        AcceptanceStructuralCoverageRequest request,
        CancellationToken cancellationToken)
    {
        var candidateDiscovery = await _discoveryRunner(
            request.CandidateDiscoveryArguments,
            request.CandidateWorktreePath,
            request.DiscoveryTimeout,
            cancellationToken).ConfigureAwait(false);
        if (candidateDiscovery.ExitCode != 0)
        {
            return new AcceptanceStructuralCoverageEvaluation(
                candidateDiscovery,
                null,
                null,
                null,
                false);
        }

        var baseline = await request.PrepareBaseline(cancellationToken).ConfigureAwait(false);
        GoalAcceptanceVerifier.CommandResult? baselineDiscovery = null;
        TestDiscoverySnapshot? baselineSnapshot = null;
        if (baseline is not null)
        {
            try
            {
                baselineDiscovery = await _discoveryRunner(
                    baseline.DiscoveryArguments,
                    baseline.WorktreePath,
                    request.DiscoveryTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_isBaselineDiscoveryIoException(ex))
            {
                return new AcceptanceStructuralCoverageEvaluation(
                    candidateDiscovery,
                    null,
                    ex,
                    null,
                    baseline.LockRemediationApplied);
            }

            if (baselineDiscovery.TimedOut || baselineDiscovery.ExitCode != 0)
            {
                return new AcceptanceStructuralCoverageEvaluation(
                    candidateDiscovery,
                    baselineDiscovery,
                    null,
                    null,
                    baseline.LockRemediationApplied);
            }

            baselineSnapshot = TestCoverageInvariant.ParseDiscovery(
                baselineDiscovery.Output,
                baseline.BareTestList,
                baseline.RepositoryRoot);
        }

        var partitions = request.ResolvePartitions();
        var candidateSnapshot = TestCoverageInvariant.ParseDiscovery(
            candidateDiscovery.Output,
            request.BareTestList);
        var deletedTestFiles = request.ResolveDeletedTestFiles();
        var coverage = TestCoverageInvariant.Evaluate(
            candidateSnapshot.Tests,
            partitions,
            baselineSnapshot?.Tests,
            deletedTestFiles,
            request.CurrentAttemptId,
            baselineSnapshot?.SourceFilesByTest,
            request.SanctionedRemovedTests);
        if (coverage.CountComparison?.IsShortfall == true &&
            coverage.EmptyPartitions.Count == 0 &&
            coverage.MissingTests.Count == 1 &&
            baselineSnapshot is not null &&
            request.PrepareContainedBaseline is not null)
        {
            var observedMainCount = baselineSnapshot.Tests.Count;
            using var contained = await request.PrepareContainedBaseline(
                cancellationToken).ConfigureAwait(false);
            if (!contained.IsResolved)
            {
                coverage = AppendGenerationReceipt(
                    coverage,
                    $"cross-generation-staleness:contained=unresolved,reason={contained.UnresolvedReason ?? "unknown"},disposition=unresolved-generation",
                    addToMissing: true);
            }
            else
            {
                var containedSnapshot = contained.UseObservedBaseline
                    ? baselineSnapshot
                    : await DiscoverContainedBaselineAsync(contained, request, cancellationToken).ConfigureAwait(false);
                if (containedSnapshot is null || containedSnapshot.Tests.Count == 0)
                {
                    coverage = AppendGenerationReceipt(
                        coverage,
                        $"cross-generation-staleness:contained=unresolved,reason={contained.UnresolvedReason ?? "discovery-unresolved"},disposition=unresolved-generation",
                        addToMissing: true);
                }
                else
                {
                    coverage = TestCoverageInvariant.Evaluate(
                        candidateSnapshot.Tests,
                        partitions,
                        containedSnapshot.Tests,
                        deletedTestFiles,
                        request.CurrentAttemptId,
                        containedSnapshot.SourceFilesByTest,
                        request.SanctionedRemovedTests);
                    var disposition = coverage.Passed ? "integration-stale" : "coverage-shortfall";
                    var receipt =
                        $"cross-generation-staleness:contained={ShortSha(contained.ContainedMainSha)},observed-main={ShortSha(contained.ObservedMainSha)},candidate={candidateSnapshot.Tests.Count},contained-minimum={coverage.CountComparison?.MinimumCandidateCount ?? containedSnapshot.Tests.Count},observed-main-count={observedMainCount},disposition={disposition}";
                    coverage = AppendGenerationReceipt(coverage, receipt, addToMissing: !coverage.Passed);
                }
            }
        }
        return new AcceptanceStructuralCoverageEvaluation(
            candidateDiscovery,
            baselineDiscovery,
            null,
            coverage,
            baseline?.LockRemediationApplied == true);
    }

    private async Task<TestDiscoverySnapshot?> DiscoverContainedBaselineAsync(
        AcceptanceContainedGenerationBaseline contained,
        AcceptanceStructuralCoverageRequest request,
        CancellationToken cancellationToken)
    {
        if (contained.Baseline is null)
        {
            contained.MarkUnresolved("baseline-unavailable");
            return null;
        }

        try
        {
            var discovery = await _discoveryRunner(
                contained.Baseline.DiscoveryArguments,
                contained.Baseline.WorktreePath,
                request.DiscoveryTimeout,
                cancellationToken).ConfigureAwait(false);
            if (discovery.TimedOut || discovery.ExitCode != 0)
            {
                contained.MarkUnresolved(discovery.TimedOut ? "discovery-timeout" : "discovery-failed");
                return null;
            }

            return TestCoverageInvariant.ParseDiscovery(
                discovery.Output,
                contained.Baseline.BareTestList,
                contained.Baseline.RepositoryRoot);
        }
        catch (Exception ex) when (_isBaselineDiscoveryIoException(ex))
        {
            contained.MarkUnresolved("discovery-io");
            return null;
        }
    }

    private static TestCoverageInvariantResult AppendGenerationReceipt(
        TestCoverageInvariantResult coverage,
        string receipt,
        bool addToMissing) =>
        coverage with
        {
            Summary = $"{coverage.Summary}; {receipt}",
            MissingTests = addToMissing ? [.. coverage.MissingTests, receipt] : coverage.MissingTests
        };

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "unavailable" : sha[..Math.Min(8, sha.Length)];
}
