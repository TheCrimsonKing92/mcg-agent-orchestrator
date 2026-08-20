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
    Func<CancellationToken, Task<AcceptanceStructuralCoverageBaseline?>> PrepareBaseline);

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
        return new AcceptanceStructuralCoverageEvaluation(
            candidateDiscovery,
            baselineDiscovery,
            null,
            coverage,
            baseline?.LockRemediationApplied == true);
    }
}
