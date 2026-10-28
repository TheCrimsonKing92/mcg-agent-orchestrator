using System.Text.Json;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SourceRevertedCandidateObservation(
    string GoalId, string AttemptId, string CandidateKey, string Identity,
    DateTimeOffset ObservedAt, string OriginalResultFingerprint, FocusedEvidenceRunResult OriginalEvidence,
    string EvidencePath, string EvidenceFingerprint, FindingEvidenceArmReceipt OriginalArm,
    DateTimeOffset? RestorationCompletedAt = null);

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    // Preserve the existing delegate API while carrying a typed invocation capability to its owner.
    internal sealed class SourceRevertFocusedRunner(
        Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, SourceRevertedEvidenceContinuation?, FocusedEvidenceRunResult> runner)
    {
        internal static Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult>?
            TryCreate(FindingEvidenceNegativeControl? mode, bool runBaselineArm,
                IReadOnlyList<string>? revertPaths, FindingEvidenceMutation? mutation,
                Func<Goal, string, DotnetBuildEnvironmentLease?, bool, CancellationToken, FindingEvidenceNegativeControl,
                    IReadOnlyList<string>?, FindingEvidenceMutation?, SourceRevertedEvidenceContinuation?, FocusedEvidenceRunResult> run)
        {
            if (runBaselineArm || mode != FindingEvidenceNegativeControl.RevertSrc ||
                revertPaths is not { Count: > 0 } || mutation is not null) return null;
            return new SourceRevertFocusedRunner((goal, request, lease, token, continuation) =>
                run(goal, request, lease, false, token, FindingEvidenceNegativeControl.RevertSrc,
                    revertPaths, null, continuation)).Run;
        }

        internal FocusedEvidenceRunResult Run(Goal goal, string request, DotnetBuildEnvironmentLease? lease, CancellationToken token) =>
            runner(goal, request, lease, token, null);
        internal FocusedEvidenceRunResult RunWithContinuation(Goal goal, string request, DotnetBuildEnvironmentLease? lease,
            CancellationToken token, SourceRevertedEvidenceContinuation? continuation) => runner(goal, request, lease, token, continuation);
    }

    internal static FocusedEvidenceRunResult RetainNegativeControlEvidence(
        FocusedEvidenceRunResult next, FocusedEvidenceRunResult prior)
    {
        if (prior.NegativeControlOutcome is null) return next;
        return next with
        {
            NegativeControlOutcome = prior.NegativeControlOutcome,
            RevertPathsRejection = prior.RevertPathsRejection,
            Arms = [.. next.Arms ?? [], .. (prior.Arms ?? []).Where(arm => arm.Arm == FindingEvidenceArm.SourceReverted)],
            Summary = $"{next.Summary}; " + (prior.RevertPathsRejection is not null ||
                prior.NegativeControlOutcome == FindingEvidenceNegativeControlOutcome.CompileRed ? prior.Summary :
                FindingEvidenceNegativeControlOutcomeJsonConverter.ToWireValue(prior.NegativeControlOutcome.Value))
        };
    }

    internal SourceRevertedEvidenceContinuation? CreateSourceRevertContinuation(
        ConductorParallelAcceptanceAttempt attempt, ConductorParallelAcceptanceCandidate candidate, bool baseline)
    {
        if (baseline || attempt.Kind != PreReviewEvidenceDispatchKind ||
            attempt.FocusedEvidenceNegativeControl != FindingEvidenceNegativeControl.RevertSrc ||
            attempt.FocusedEvidenceRevertPaths is not { Count: > 0 } || attempt.FocusedEvidenceMutation is not null ||
            attempt.GoalId != candidate.Goal.Id.Value || attempt.CandidateKey != candidate.CandidateKey)
            return null;
        SourceRevertedCandidateObservation? observation = null;
        string Bind(string identity) => SourceRevertedEvidenceContinuation.Fingerprint(new
        {
            Identity = identity, attempt.GoalId, attempt.CandidateKey, attempt.PolicyJson,
            attempt.FocusedExecutionBasisIdentity, attempt.IntegrationBranch, attempt.BriefVersion,
            attempt.FindingRoundFingerprint, attempt.ScopePaths
        });
        return new SourceRevertedEvidenceContinuation(
            identity =>
            {
                var retained = ReadSourceRevertCandidate(attempt, Bind(identity));
                observation = retained?.Observation;
                if (observation is not null) StoreObservation(attempt, observation);
                return retained?.Evidence;
            },
            (identity, evidence) =>
            {
                if (!evidence.IsValidEvidence || evidence.Arms is not { Count: 1 } ||
                    evidence.Arms[0] is not { Arm: FindingEvidenceArm.Candidate, Disposition: FindingEvidenceArmDisposition.Green,
                        Accepted: true, Passed: true } arm || arm.NotRunSelections is { Count: > 0 } ||
                    !string.Equals(arm.Sha, attempt.BranchHeadSha, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    // Copy while the execution owner still holds custody. Never hash historic bytes
                    // in a reader to fill missing original metadata.
                    var originalFingerprint = SourceRevertedEvidenceContinuation.Fingerprint(evidence);
                    var directory = attempt.ResultPath + ".candidate-artifacts";
                    Directory.CreateDirectory(directory);
                    var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string Copy(string path)
                    {
                        if (copies.TryGetValue(path, out var existing)) return existing;
                        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Candidate artifact path is not absolute.");
                        var destination = Path.Combine(directory, copies.Count + Path.GetExtension(path));
                        using (var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var target = File.Create(destination)) source.CopyTo(target);
                        copies.Add(path, destination);
                        return destination;
                    }
                    var checks = arm.Checks.Select(check => check with
                    {
                        TestResultPaths = check.TestResultPaths?.Select(Copy).ToArray(),
                        ArtifactsPath = string.IsNullOrWhiteSpace(check.ArtifactsPath) ? null : Copy(check.ArtifactsPath)
                    }).ToArray();
                    var retainedArm = arm with { Checks = checks };
                    var originalArm = ConductorDriver.CreateFindingEvidenceArmReceipt(retainedArm);
                    if (originalArm.ReceiptArtifacts is not { Count: > 0 } || originalArm.ExecutedTestCount is not > 0) return;
                    originalArm = originalArm with { ReceiptArtifacts = copies.Values.Select(path =>
                    {
                        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return new AcceptanceCohortEvidenceArtifact(Path.GetExtension(path) == ".trx" ? "trx" : "artifact",
                            path, Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
                    }).ToArray() };
                    var retained = evidence with { Checks = checks, Arms = [retainedArm] };
                    var path = attempt.ResultPath + ".candidate";
                    var artifact = ToArtifact(ConductorParallelAcceptanceRunResult.Focused(candidate, retained));
                    WriteResult(path, artifact);
                    observation = new(attempt.GoalId, attempt.AttemptId, attempt.CandidateKey, Bind(identity),
                        _utcNow(), originalFingerprint, evidence, path, HashReplayResult(JsonSerializer.Serialize(artifact, JsonOptions)), originalArm);
                    StoreObservation(attempt, observation);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    observation = null; // Retention is an optimization; the requested arm still executes.
                }
            },
            restored =>
            {
                if (observation is null || !restored) return;
                // Referenced observations retain their original owner and observation time.
                var restoredAt = _utcNow();
                if (observation.AttemptId == attempt.AttemptId)
                    observation = observation with { RestorationCompletedAt = restoredAt };
                StoreObservation(attempt, observation, restoredAt);
            });
    }

    private void StoreObservation(ConductorParallelAcceptanceAttempt attempt, SourceRevertedCandidateObservation observation,
        DateTimeOffset? restorationCompletedAt = null)
    {
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.AttemptId != attempt.AttemptId || current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
                throw new InvalidDataException("Partial candidate observation lost its owning running attempt.");
            WriteAttemptFile(current with
            {
                PartialSourceRevertCandidate = observation,
                PartialSourceRevertRestorationCompletedAt = restorationCompletedAt
            });
        }
    }

    private (SourceRevertedCandidateObservation Observation, FocusedEvidenceRunResult Evidence)? ReadSourceRevertCandidate(
        ConductorParallelAcceptanceAttempt current, string identity)
    {
        try
        {
            var attempts = ReadFocusedCoverageAttempts(new GoalId(current.GoalId));
            var previous = attempts.Where(a => a.Ordinal < current.Ordinal && a.CandidateKey == current.CandidateKey)
                .OrderByDescending(a => a.Ordinal).FirstOrDefault(a => a.PartialSourceRevertCandidate is not null);
            if (previous?.PartialSourceRevertCandidate is not { } observation || observation.Identity != identity ||
                observation.GoalId != current.GoalId || observation.CandidateKey != current.CandidateKey ||
                observation.OriginalArm is null || observation.OriginalEvidence is null ||
                string.IsNullOrWhiteSpace(observation.OriginalResultFingerprint) ||
                SourceRevertedEvidenceContinuation.Fingerprint(observation.OriginalEvidence) != observation.OriginalResultFingerprint) return null;
            var original = attempts.SingleOrDefault(a => a.AttemptId == observation.AttemptId);
            if (original?.PartialSourceRevertCandidate is not { } authority ||
                original.CandidateKey != observation.CandidateKey ||
                SourceRevertedEvidenceContinuation.Fingerprint(authority) != SourceRevertedEvidenceContinuation.Fingerprint(observation) ||
                original.SupersededBy is not null || original.Outcome != ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot ||
                original.CompletedAt is null || observation.RestorationCompletedAt is null ||
                observation.ObservedAt < original.StartedAt || observation.ObservedAt > observation.RestorationCompletedAt ||
                observation.RestorationCompletedAt > original.CompletedAt || original.CompletedAt > current.StartedAt ||
                original.Ordinal >= current.Ordinal ||
                observation.EvidencePath != original.ResultPath + ".candidate") return null;
            // A later non-green applicable outcome vetoes the old green. Repeated capacity
            // deferrals are allowed only when they carry this same original observation.
            foreach (var later in attempts.Where(a => a.Ordinal > original.Ordinal && a.Ordinal < current.Ordinal &&
                         a.CandidateKey == current.CandidateKey))
                if (later.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed &&
                    !(later.Outcome == ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot &&
                      later.PartialSourceRevertCandidate is { } partial &&
                      SourceRevertedEvidenceContinuation.Fingerprint(partial) == SourceRevertedEvidenceContinuation.Fingerprint(observation) &&
                      later.CompletedAt <= current.StartedAt && ValidPartialResult(later)))
                    return null;
            if (previous.CompletedAt > current.StartedAt ||
                !ValidPartialResult(original) || !ValidPartialResult(previous)) return null;
            var evidenceText = File.ReadAllText(observation.EvidencePath);
            var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(evidenceText, JsonOptions);
            if (artifact is not { Kind: "focused-evidence", FocusedEvidence: { IsValidEvidence: true, Arms.Count: 1 } evidence } ||
                HashReplayResult(evidenceText) != observation.EvidenceFingerprint ||
                evidence.Arms[0] is not { Arm: FindingEvidenceArm.Candidate, Disposition: FindingEvidenceArmDisposition.Green,
                    Accepted: true, Passed: true, Checks.Count: > 0 } arm ||
                !string.Equals(arm.Sha, current.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
                observation.OriginalArm is not { Arm: FindingEvidenceArm.Candidate, Disposition: FindingEvidenceArmDisposition.Green,
                    Accepted: true, Passed: true, ExecutedTestCount: > 0 } ||
                observation.OriginalArm.Sha != arm.Sha || observation.OriginalArm.NotRunSelections.Count > 0 ||
                observation.OriginalArm.ReceiptArtifacts is not { Count: > 0 } receipts ||
                receipts.Any(receipt => receipt is null) ||
                observation.OriginalArm.TestResultPaths is not { Count: > 0 } trxPaths ||
                !trxPaths.Order(StringComparer.Ordinal).SequenceEqual(arm.Checks.SelectMany(c => c.TestResultPaths ?? []).Order(StringComparer.Ordinal)) ||
                arm.Checks.Any(c => !c.Passed || c.ExecutedTestCount is not > 0) ||
                !observation.OriginalArm.ReceiptPaths!.Order(StringComparer.Ordinal).SequenceEqual(receipts.Select(r => r.Path).Order(StringComparer.Ordinal))) return null;
            foreach (var receipt in receipts)
            {
                if (!Path.GetFullPath(receipt.Path).StartsWith(Path.GetFullPath(original.ResultPath + ".candidate-artifacts") +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                using var stream = File.Open(receipt.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != receipt.Length || Convert.ToHexStringLower(SHA256.HashData(stream)) != receipt.Sha256) return null;
            }
            return (observation, evidence);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ValidPartialResult(ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot || attempt.CompletedAt is null ||
            attempt.ReplayResultFingerprint is null || attempt.PartialSourceRevertRestorationCompletedAt is not { } restored ||
            restored < attempt.StartedAt || restored > attempt.CompletedAt) return false;
        var text = File.ReadAllText(attempt.ResultPath);
        var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(text, JsonOptions);
        return artifact is { Kind: "fault", FaultKind: "blocked-build-slot" } &&
            artifact.BranchHeadSha == attempt.BranchHeadSha && artifact.MainHeadSha == attempt.MainHeadSha &&
            HashReplayResult(text) == attempt.ReplayResultFingerprint;
    }

    // Original authority is captured by the execution owner, never reconstructed by a reader.
    private static FindingEvidenceReceipt? CaptureOriginalFocusedReceipt(
        ConductorParallelAcceptanceAttempt attempt, ConductorParallelAcceptanceRunResult run)
    {
        if (attempt.FindingRoundFingerprint != "pre-tester-deferred" ||
            string.IsNullOrWhiteSpace(attempt.FocusedExecutionBasisIdentity) ||
            string.IsNullOrWhiteSpace(attempt.FocusedEvidenceReceiptId) ||
            run.FocusedEvidence is not { } evidence ||
            !string.Equals(attempt.BranchHeadSha, run.Candidate.BranchHeadSha, StringComparison.OrdinalIgnoreCase)) return null;
        var selections = (attempt.FocusedEvidenceMemberRequests ?? []).SelectMany(member =>
            ConductorDriver.ParseDispositionSelections(member)).ToArray();
        if (selections.Length == 0) return null;
        var request = new FindingEvidenceRequest(selections);
        var identity = ConductorDriver.BuildFindingEvidenceIdentity(request);
        return new(attempt.FocusedEvidenceReceiptId, attempt.BranchHeadSha!, request,
            evidence.Accepted, evidence.IsValidEvidence, evidence.Summary,
            (evidence.Arms ?? []).Select(ConductorDriver.CreateFindingEvidenceArmReceipt).ToArray(),
            [new("pre-tester-deferred", identity, "executed-pre-tester", "pre-tester-deferred",
                FindingEvidenceRequestDecision.Executed, attempt.BranchHeadSha, attempt.FindingRoundFingerprint,
                SourceReceiptIds: [attempt.FocusedEvidenceReceiptId],
                ExecutionBasisIdentity: attempt.FocusedExecutionBasisIdentity)],
            attempt.FindingRoundFingerprint, attempt.FocusedExecutionBasisIdentity);
    }

    internal IReadOnlyList<ConductorParallelAcceptanceAttempt> ReadFocusedCoverageAttempts(GoalId goalId)
    {
        var directory = Path.Combine(_rootDirectory, goalId.Value);
        if (!Directory.Exists(directory)) return [];
        // A corrupt canonical record invalidates this optimization as a whole: silently skipping
        // it could expose an older GREEN hidden by a newer failed attempt.
        try
        {
            return Directory.EnumerateFiles(directory, "*.attempt.json")
                .Select(path => ReadCanonicalAttempt(path, goalId.Value))
                .Where(attempt => attempt.Kind == PreReviewEvidenceDispatchKind)
                .OrderBy(attempt => attempt.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return [];
        }
    }

    internal FindingEvidenceReceipt? ReadOriginalFocusedReceipt(ConductorParallelAcceptanceAttempt attempt)
    {
        try
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (current.AttemptId != attempt.AttemptId || current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed ||
                current.SupersededBy is not null || current.CompletedAt is null ||
                current.OriginalFocusedReceipt is not { } receipt ||
                receipt.ReceiptId != current.FocusedEvidenceReceiptId ||
                receipt.ExecutionBasisIdentity != current.FocusedExecutionBasisIdentity ||
                receipt.FindingRoundFingerprint != "pre-tester-deferred" ||
                !string.Equals(receipt.CandidateSha, current.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(current.ReplayResultFingerprint)) return null;
            var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                File.ReadAllText(current.ResultPath), JsonOptions);
            if (artifact is not { Kind: "focused-evidence", FocusedEvidence: { Accepted: true, IsValidEvidence: true } } ||
                !string.Equals(artifact.BranchHeadSha, current.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
                HashReplayResult(JsonSerializer.Serialize(artifact, JsonOptions)) != current.ReplayResultFingerprint) return null;
            return receipt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    // A reconciled capacity deferral still owns its batch, even though it has no evidence receipt.
    internal ConductorParallelAcceptanceAttempt? ReadLatestFocusedEvidenceAttempt(
        ConductorParallelAcceptanceCandidate candidate, string batchId)
    {
        var directory = Path.Combine(_rootDirectory, candidate.Goal.Id.Value);
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*.attempt.json")
            .Select(path => ReadCanonicalAttempt(path, candidate.Goal.Id.Value))
            .Where(attempt => attempt.Kind == PreReviewEvidenceDispatchKind &&
                attempt.CandidateKey == candidate.CandidateKey && attempt.FocusedEvidenceBatchId == batchId)
            .OrderByDescending(attempt => attempt.Ordinal)
            .FirstOrDefault();
    }

    // Include reconciled attempts: reconciliation does not revoke a receipt's evidence.
    internal FocusedEvidenceRunResult? ReadFocusedReceiptEvidence(GoalId goalId, string receiptId, string candidateSha)
    {
        var directory = Path.Combine(_rootDirectory, goalId.Value);
        if (!Directory.Exists(directory)) return null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
            {
                var attempt = ReadCanonicalAttempt(path, goalId.Value);
                if (attempt.FocusedEvidenceReceiptId != receiptId ||
                    !string.Equals(attempt.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase)) continue;
                var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                    File.ReadAllText(attempt.ResultPath), JsonOptions);
                return artifact is { Kind: "focused-evidence", FocusedEvidence: { } evidence } &&
                    string.Equals(artifact.BranchHeadSha, candidateSha, StringComparison.OrdinalIgnoreCase)
                    ? evidence : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Missing or unreadable provenance cannot authorize a recovery rerun.
            return null;
        }
        return null;
    }
}
