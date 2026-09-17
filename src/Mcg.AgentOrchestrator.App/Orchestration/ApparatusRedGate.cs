using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// Everything the acceptance failure handler learned about one gate completion. Produced by the
/// index write at gate entry and consumed by the classification read, so the expensive work — TRX
/// reads and test-source resolution — happens exactly once per advance.
/// </summary>
internal sealed record ApparatusRedGateReading(
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ApparatusRedFailingTest> FailingTests,
    bool EveryFailedCheckHasTestIdentities,
    IReadOnlyList<string> FailedCheckNames);

/// <summary>
/// The composition root for apparatus-RED classification: it owns the census store, the test-source
/// resolver, the clock, the cross-goal window and the per-goal re-gate bound. It is the single type
/// <see cref="ConductorDriver"/> references, so the classification verdict and its evidence stay out
/// of the ratcheted driver.
/// </summary>
internal sealed class ApparatusRedGate
{
    internal const int DefaultPerGoalRegateCap = 2;

    // No historical census exists to justify a number — this index is the artifact that creates one.
    // The incident narrative ("the fourth time in three days") is the only signal; revisit after a
    // week of real data.
    internal static readonly TimeSpan DefaultCrossGoalWindow = TimeSpan.FromHours(72);

    private readonly AcceptanceFailingTestIndex _index;
    private readonly Func<Goal, string?> _resolveSourceRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _crossGoalWindow;
    private readonly int _perGoalRegateCap;

    internal ApparatusRedGate(
        string indexPath,
        Func<Goal, string?>? resolveSourceRoot = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? crossGoalWindow = null,
        int perGoalRegateCap = DefaultPerGoalRegateCap)
        : this(new AcceptanceFailingTestIndex(indexPath), resolveSourceRoot, utcNow, crossGoalWindow, perGoalRegateCap)
    {
    }

    internal ApparatusRedGate(
        AcceptanceFailingTestIndex index,
        Func<Goal, string?>? resolveSourceRoot = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? crossGoalWindow = null,
        int perGoalRegateCap = DefaultPerGoalRegateCap)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfNegative(perGoalRegateCap);
        _index = index;
        _resolveSourceRoot = resolveSourceRoot ?? (_ => null);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _crossGoalWindow = crossGoalWindow ?? DefaultCrossGoalWindow;
        _perGoalRegateCap = perGoalRegateCap;
    }

    internal int PerGoalRegateCap => _perGoalRegateCap;

    internal AcceptanceFailingTestIndex Index => _index;

    /// <summary>
    /// Records this gate completion in the cross-goal census and returns the materialized evidence.
    /// Called at the acceptance failure handler's single entry point so it runs for every gate
    /// completion — including the environmental-apparatus and unattributable early returns — which is
    /// what makes the second occurrence of a flake recognisable without reading TRX files.
    /// </summary>
    internal ApparatusRedGateReading? RecordGateCompletion(
        Goal goal,
        AcceptanceVerificationSummary acceptance,
        Lazy<IReadOnlyList<string>> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(acceptance);
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (acceptance.RequiredUnmetCriteria.Count == 0)
        {
            return null;
        }

        try
        {
            return RecordGateCompletionCore(goal, acceptance, changedPaths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An index or resolver failure must never fail or alter a gate outcome.
            return null;
        }
    }

    /// <summary>
    /// Reads the census written above, resolves the cross-goal rule and the per-goal bound, and
    /// returns the verdict. Called strictly between the actionable-criteria confirmation and the
    /// Developer reopen, so a genuine RED reaches today's path byte-identically.
    /// </summary>
    internal ApparatusRedDisposition Classify(Goal goal, ApparatusRedGateReading? reading)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (reading is null)
        {
            return new ApparatusRedDisposition.Genuine("no gate reading was recorded for this advance");
        }

        var now = _utcNow();
        var records = _index.Read();
        var failingTests = reading.FailingTests
            .Select(failingTest => failingTest with
            {
                HasCrossGoalOccurrence = AcceptanceFailingTestIndex.HasCrossGoalOccurrence(
                    records,
                    goal.Id.Value,
                    failingTest.TestIdentity,
                    now,
                    _crossGoalWindow)
            })
            .ToArray();
        return ApparatusRedClassifier.Classify(new ApparatusRedEvidence(
            reading.ChangedPaths,
            failingTests,
            reading.EveryFailedCheckHasTestIdentities,
            AcceptanceFailingTestIndex.CountRegates(records, goal.Id.Value),
            _perGoalRegateCap));
    }

    /// <summary>Durably records one apparatus re-gate so the per-goal bound survives a restart.</summary>
    internal void RecordRegate(Goal goal, ApparatusRedDisposition.Regate regate)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(regate);
        _index.Append(
            [
                new AcceptanceFailingTestIndexRecord(
                    AcceptanceFailingTestIndex.ContractVersion,
                    AcceptanceFailingTestIndexKinds.ApparatusRegate,
                    goal.Id.Value,
                    _utcNow(),
                    TestIdentity: string.Join(", ", regate.TestIdentities),
                    EvidenceKind: regate.EvidenceKind)
            ],
            _utcNow());
    }

    private ApparatusRedGateReading RecordGateCompletionCore(
        Goal goal,
        AcceptanceVerificationSummary acceptance,
        Lazy<IReadOnlyList<string>> changedPaths)
    {
        var sourceRoot = _resolveSourceRoot(goal);
        var normalizedChangedPaths = changedPaths.Value
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        var failingTests = new List<ApparatusRedFailingTest>();
        var everyCheckHasIdentities = true;
        foreach (var check in acceptance.RequiredUnmetCriteria)
        {
            var identities = check.FailingTestIdentities?
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            if (identities.Length == 0)
            {
                everyCheckHasIdentities = false;
                continue;
            }

            var trxFailures = ReadTrxFailures(check);
            foreach (var identity in identities)
            {
                var failure = trxFailures.FirstOrDefault(candidate => IdentityMatches(candidate.TestName, identity));
                var signature = failure is null
                    ? ApparatusInfrastructureSignatures.Match(check.OutputTail)
                    : ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace);
                var sourcePaths = AcceptanceTestSourceResolver.ResolveSourcePaths(
                    sourceRoot,
                    check.TestProjectPath,
                    identity);
                failingTests.Add(new ApparatusRedFailingTest(
                    check.Name,
                    identity,
                    signature,
                    sourcePaths,
                    sourcePaths.Any(path => normalizedChangedPaths.Contains(
                        NormalizePath(path),
                        StringComparer.OrdinalIgnoreCase)),
                    HasCrossGoalOccurrence: false));
            }
        }

        var recordedAt = _utcNow();
        _index.Append(
            failingTests
                .Select(failingTest => new AcceptanceFailingTestIndexRecord(
                    AcceptanceFailingTestIndex.ContractVersion,
                    AcceptanceFailingTestIndexKinds.GateFailure,
                    goal.Id.Value,
                    recordedAt,
                    CandidateSha: acceptance.BranchHeadSha,
                    CheckName: failingTest.CheckName,
                    TestIdentity: failingTest.TestIdentity,
                    ExceptionSignature: failingTest.ExceptionSignature,
                    ResolvedSourcePath: failingTest.ResolvedSourcePaths.FirstOrDefault(),
                    InsideChangedPaths: failingTest.InsideChangedPaths))
                .ToArray(),
            recordedAt);

        return new ApparatusRedGateReading(
            normalizedChangedPaths,
            failingTests,
            everyCheckHasIdentities,
            acceptance.FailedChecks is { Count: > 0 } failedChecks
                ? failedChecks
                : acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray());
    }

    private static IReadOnlyList<AcceptanceTrxFailure> ReadTrxFailures(AcceptanceCheckResult check)
    {
        if (check.TestResultPaths is not { Count: > 0 } paths)
        {
            return [];
        }

        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(path => AcceptanceTrxFailureReader.Read(path).Failures)
            .ToArray();
    }

    private static bool IdentityMatches(string? trxTestName, string identity)
    {
        if (string.IsNullOrWhiteSpace(trxTestName))
        {
            return false;
        }

        return trxTestName.Equals(identity, StringComparison.Ordinal) ||
            trxTestName.EndsWith('.' + identity, StringComparison.Ordinal) ||
            identity.EndsWith('.' + trxTestName, StringComparison.Ordinal);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('.', '/');
}
