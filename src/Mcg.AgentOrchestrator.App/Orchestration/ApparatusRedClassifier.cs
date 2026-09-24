namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>One failing test of a RED, with every input the classification needs already resolved.</summary>
internal sealed record ApparatusRedFailingTest(
    string CheckName,
    string TestIdentity,
    string? ExceptionSignature,
    IReadOnlyList<string> ResolvedSourcePaths,
    bool InsideChangedPaths,
    bool HasCrossGoalOccurrence,
    bool CandidateRerunPassed = false,
    bool CandidateRerunFailed = false);

/// <summary>Materialized classification inputs. No I/O happens below this record.</summary>
internal sealed record ApparatusRedEvidence(
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ApparatusRedFailingTest> FailingTests,
    bool EveryFailedCheckHasTestIdentities,
    int RegateCount,
    int RegateCap);

internal abstract record ApparatusRedDisposition
{
    /// <summary>Take today's path unchanged: reopen the Developer exactly as before.</summary>
    internal sealed record Genuine(string Reason) : ApparatusRedDisposition;

    /// <summary>Restore Verified for a re-gate on the next tick, like a typed apparatus fault.</summary>
    internal sealed record Regate(
        string EvidenceKind,
        IReadOnlyList<string> TestIdentities,
        int RegateOrdinal,
        int RegateCap) : ApparatusRedDisposition;

    /// <summary>The per-goal re-gate bound is spent: escalate to the operator, never to a paid round.</summary>
    internal sealed record BoundExhausted(
        string EvidenceKind,
        IReadOnlyList<string> TestIdentities,
        int RegateCount,
        int RegateCap) : ApparatusRedDisposition;
}

internal static class ApparatusRedClassifier
{
    internal const string CrossGoalEvidenceKind = "cross-goal-flake";
    internal const string CandidateRerunEvidenceKind = "candidate-rerun-pass";
    internal const string BoundExhaustedToken = "apparatus-regate-bound-exhausted";

    /// <summary>
    /// A RED is apparatus only if the whole conjunction holds. Every gap fails closed to
    /// <see cref="ApparatusRedDisposition.Genuine"/>: misclassifying a genuine RED as apparatus would
    /// silently swallow a real failure, which is far costlier than the paid round this replaces.
    /// </summary>
    internal static ApparatusRedDisposition Classify(ApparatusRedEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.ChangedPaths.Count == 0)
        {
            return new ApparatusRedDisposition.Genuine(
                "candidate changed paths are unknown, so the candidate/apparatus boundary cannot be drawn");
        }

        if (!evidence.EveryFailedCheckHasTestIdentities)
        {
            return new ApparatusRedDisposition.Genuine(
                "a failing check carries no test identity, so there is nothing to locate outside the candidate");
        }

        if (evidence.FailingTests.Count == 0)
        {
            return new ApparatusRedDisposition.Genuine("the RED carries no failing test identities");
        }

        var evidenceKinds = new List<string>();
        foreach (var failingTest in evidence.FailingTests)
        {
            if (failingTest.ResolvedSourcePaths.Count == 0)
            {
                return new ApparatusRedDisposition.Genuine(
                    $"failing test {failingTest.TestIdentity} could not be placed in a source file");
            }

            if (failingTest.InsideChangedPaths)
            {
                return new ApparatusRedDisposition.Genuine(
                    $"failing test {failingTest.TestIdentity} lives inside the candidate's changed paths");
            }

            if (!string.IsNullOrWhiteSpace(failingTest.ExceptionSignature))
            {
                evidenceKinds.Add(ApparatusInfrastructureSignatures.EvidenceKind);
                continue;
            }

            if (failingTest.HasCrossGoalOccurrence)
            {
                evidenceKinds.Add(CrossGoalEvidenceKind);
                continue;
            }

            if (failingTest.CandidateRerunFailed)
            {
                return new ApparatusRedDisposition.Genuine(
                    $"failing test {failingTest.TestIdentity} failed again on the candidate");
            }

            if (failingTest.CandidateRerunPassed)
            {
                evidenceKinds.Add(CandidateRerunEvidenceKind);
                continue;
            }

            return new ApparatusRedDisposition.Genuine(
                $"failing test {failingTest.TestIdentity} has neither a recorded infrastructure signature " +
                "nor a prior cross-goal occurrence");
        }

        var evidenceKind = string.Join(
            "+",
            evidenceKinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var identities = evidence.FailingTests
            .Select(failingTest => failingTest.TestIdentity)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return evidence.RegateCount >= evidence.RegateCap
            ? new ApparatusRedDisposition.BoundExhausted(
                evidenceKind,
                identities,
                evidence.RegateCount,
                evidence.RegateCap)
            : new ApparatusRedDisposition.Regate(
                evidenceKind,
                identities,
                evidence.RegateCount + 1,
                evidence.RegateCap);
    }
}
