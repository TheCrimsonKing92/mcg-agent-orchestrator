using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ReviewFindingProjectedBody(string LogicalIdentity, string Sha256, byte[] Bytes);

internal sealed record ReviewFindingContextProjection(
    byte[] LedgerBytes,
    IReadOnlyList<ReviewFindingProjectedBody> RoundBodies,
    IReadOnlyList<ReviewFindingProjectedBody> ReceiptBodies,
    byte[]? ContractRepairEnvelopeBytes,
    ReviewFindingHistoryProjectionMetrics Metrics);

internal static class ReviewFindingContextProjector
{
    private sealed record RoundSource(TaskSpec Task, TaskVerificationRecord Verification);
    private sealed record FindingSource(TaskSpec Task, TaskVerificationRecord Verification, ReviewFinding Finding, ReviewFindingContentReference Round);

    public static void AddArtifacts(
        ReviewFindingContextProjection projection,
        Action<WorkerProfileDispatcher.WorkerContextSemanticSource, string, ContextArtifactKind, byte[], IReadOnlyList<AgentRole>?, ContextDeliveryMode?> addSource)
    {
        addSource(WorkerProfileDispatcher.WorkerContextSemanticSource.ReviewFindingHistory, "goal/review-finding-history.json", ContextArtifactKind.AcceptanceCriteria, projection.LedgerBytes, null, ContextDeliveryMode.InlineFull);
        foreach (var body in projection.RoundBodies.Concat(projection.ReceiptBodies))
        {
            addSource(WorkerProfileDispatcher.WorkerContextSemanticSource.ReviewFindingHistory, body.LogicalIdentity, ContextArtifactKind.RegisteredContext, body.Bytes, null, ContextDeliveryMode.MandatoryFile);
        }
        if (projection.ContractRepairEnvelopeBytes is not null)
        {
            addSource(WorkerProfileDispatcher.WorkerContextSemanticSource.ReviewFindingHistory, "task/review-contract-repair-envelope.json", ContextArtifactKind.RoleOutputContract, projection.ContractRepairEnvelopeBytes, null, ContextDeliveryMode.InlineFull);
        }
    }

    public static void ApplyCompactArtifactAllowList(List<WorkerContextArtifact> artifacts) =>
        artifacts.RemoveAll(artifact =>
            artifact.Identity.Value != "goal/review-finding-history.json" &&
            artifact.Identity.Value != "task/review-contract-repair-envelope.json" &&
            !artifact.Identity.Value.StartsWith("goal/review-finding-rounds/", StringComparison.Ordinal) &&
            !artifact.Identity.Value.StartsWith("goal/review-finding-receipts/", StringComparison.Ordinal));

    public static ReviewFindingContextProjection Project(Goal goal, TaskSpec targetTask, string? currentCandidateSha)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(targetTask);
        var rounds = goal.Tasks
            .SelectMany(task => task.VerificationHistory.Select(verification => new RoundSource(task, verification)))
            .Where(source => (source.Verification.MergedReviewFindings?.Count ?? 0) > 0 ||
                             (source.Verification.FindingEvidenceReceipts?.Count ?? 0) > 0)
            .OrderBy(source => source.Verification.CompletedAt)
            .ThenBy(source => source.Task.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (rounds.Length == 0)
        {
            throw new WorkerContextPreparationException(
                new LogicalArtifactIdentity("goal/review-finding-history.json"),
                "required-history-missing",
                "Review finding projection was requested but no authoritative finding rounds were available.");
        }

        var receiptBodies = new Dictionary<string, ReviewFindingProjectedBody>(StringComparer.Ordinal);
        var receiptReferenceById = new Dictionary<string, ReviewFindingContentReference>(StringComparer.Ordinal);
        var totalReceiptCount = 0;
        foreach (var receipt in rounds.SelectMany(round => round.Verification.FindingEvidenceReceipts ?? []))
        {
            totalReceiptCount++;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
            var hash = WorkerContextArtifact.Hash(bytes);
            var reference = new ReviewFindingContentReference(hash, $"goal/review-finding-receipts/{hash}.json");
            receiptBodies.TryAdd(hash, new ReviewFindingProjectedBody(reference.LogicalIdentity, hash, bytes));
            if (receiptReferenceById.TryGetValue(receipt.ReceiptId, out var prior) && prior != reference)
            {
                throw new WorkerContextPreparationException(
                    new LogicalArtifactIdentity("goal/review-finding-history.json"),
                    "evidence-identity-conflict",
                    $"Evidence receipt id '{receipt.ReceiptId}' resolves to different immutable bodies.");
            }
            receiptReferenceById[receipt.ReceiptId] = reference;
        }

        var roundBodies = new Dictionary<string, ReviewFindingProjectedBody>(StringComparer.Ordinal);
        var roundReferenceBySource = new Dictionary<RoundSource, ReviewFindingContentReference>();
        var roundIndex = new List<ReviewFindingRoundIndexEntry>();
        foreach (var source in rounds)
        {
            var receiptReferences = (source.Verification.FindingEvidenceReceipts ?? [])
                .Select(receipt => receiptReferenceById[receipt.ReceiptId])
                .OrderBy(reference => reference.Sha256, StringComparer.Ordinal)
                .ToArray();
            var verdictIdentity = BuildVerdictIdentity(source.Verification);
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                CandidateSha = source.Verification.ReviewedCommit,
                VerdictIdentity = verdictIdentity,
                Findings = source.Verification.MergedReviewFindings ?? [],
                TouchedAnchors = source.Verification.ReviewFindingTouchedAnchors ?? [],
                source.Verification.ReviewFindingTouchProofDiagnostic,
                ReceiptBodies = receiptReferences
            });
            var hash = WorkerContextArtifact.Hash(bodyBytes);
            var reference = new ReviewFindingContentReference(hash, $"goal/review-finding-rounds/{hash}.json");
            roundBodies.TryAdd(hash, new ReviewFindingProjectedBody(reference.LogicalIdentity, hash, bodyBytes));
            roundReferenceBySource[source] = reference;
            roundIndex.Add(new ReviewFindingRoundIndexEntry(
                source.Task.Id.Value,
                source.Task.RequiredRole,
                source.Verification.CompletedAt,
                source.Verification.ReviewedCommit,
                verdictIdentity,
                reference));
        }

        var findingSources = rounds.SelectMany(source =>
            (source.Verification.MergedReviewFindings ?? []).Select(finding =>
                new FindingSource(source.Task, source.Verification, finding, roundReferenceBySource[source]))).ToArray();
        var entries = findingSources
            .GroupBy(source => source.Finding.StableId, StringComparer.Ordinal)
            .Select(BuildCanonicalEntry)
            .OrderBy(entry => entry.StableId, StringComparer.Ordinal)
            .ToArray();

        var repairRequested = targetTask.PendingRetryRoundKind == RetryRoundKind.Mechanical &&
                              targetTask.LastVerification?.ReviewFindingContractViolation is not null;
        var fallbackReason = DetermineFallbackReason(repairRequested, targetTask.LastVerification, currentCandidateSha);
        var mode = repairRequested && fallbackReason is null
            ? ReviewFindingHistoryProjectionMode.ContractRepair
            : ReviewFindingHistoryProjectionMode.FullInspection;
        var metrics = new ReviewFindingHistoryProjectionMetrics(
            mode,
            roundBodies.Count,
            rounds.Length - roundBodies.Count,
            receiptBodies.Count,
            totalReceiptCount - receiptBodies.Count,
            fallbackReason);
        var ledger = new ReviewFindingHistoryLedger(
            ContextContractVersion.V1.Value,
            mode,
            fallbackReason,
            entries,
            roundIndex,
            receiptBodies.Values
                .OrderBy(body => body.Sha256, StringComparer.Ordinal)
                .Select(body => new ReviewFindingContentReference(body.Sha256, body.LogicalIdentity))
                .ToArray());
        var ledgerBytes = JsonSerializer.SerializeToUtf8Bytes(ledger);

        byte[]? envelopeBytes = null;
        if (mode == ReviewFindingHistoryProjectionMode.ContractRepair)
        {
            var verification = targetTask.LastVerification!;
            var references = roundBodies.Values.Concat(receiptBodies.Values)
                .OrderBy(body => body.Sha256, StringComparer.Ordinal)
                .Select(body => new ReviewFindingContentReference(body.Sha256, body.LogicalIdentity))
                .ToArray();
            var envelope = ReviewFindingContractRepairEnvelopeBuilder.Build(
                currentCandidateSha!.Trim(),
                verification.CompletionVerdictRule ?? $"exit-code:{verification.ExitCode}",
                entries,
                verification.ReviewFindingTouchedAnchors ?? [],
                verification.ReviewFindingContractViolation!,
                references);
            envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        }

        return new ReviewFindingContextProjection(
            ledgerBytes,
            roundBodies.Values.OrderBy(body => body.Sha256, StringComparer.Ordinal).ToArray(),
            receiptBodies.Values.OrderBy(body => body.Sha256, StringComparer.Ordinal).ToArray(),
            envelopeBytes,
            metrics);
    }

    private static CanonicalReviewFindingEntry BuildCanonicalEntry(IGrouping<string, FindingSource> group)
    {
        var roles = group.Select(source => source.Task.RequiredRole).Distinct().ToArray();
        if (roles.Length != 1)
        {
            throw PreparationFailure("stable-identity-role-conflict", $"Stable finding '{group.Key}' was emitted by multiple roles.");
        }

        var latestAt = group.Max(source => source.Verification.CompletedAt);
        var latest = group.Where(source => source.Verification.CompletedAt == latestAt).ToArray();
        var distinctLatest = latest
            .Select(source => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(source.Finding)))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctLatest != 1)
        {
            throw PreparationFailure("stable-identity-ambiguous", $"Stable finding '{group.Key}' has contradictory latest states at the same timestamp.");
        }

        var selected = latest.OrderBy(source => source.Task.Id.Value, StringComparer.Ordinal).First();
        var finding = selected.Finding;
        var anchorProof = finding.State == ReviewFindingState.Resolved
            ? (selected.Verification.ReviewFindingTouchedAnchors ?? [])
                .Where(anchor => anchor == finding.Location)
                .ToArray()
            : [];
        if (finding.State == ReviewFindingState.Resolved && anchorProof.Length == 0)
        {
            throw PreparationFailure("resolved-anchor-proof-missing", $"Resolved finding '{group.Key}' has no matching touched-anchor proof.");
        }

        var receiptReferences = (selected.Verification.FindingEvidenceReceipts ?? [])
            .Where(receipt => finding.EvidenceOutcome?.ReceiptId is null ||
                              string.Equals(receipt.ReceiptId, finding.EvidenceOutcome.ReceiptId, StringComparison.Ordinal))
            .Select(receipt =>
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
                var hash = WorkerContextArtifact.Hash(bytes);
                return new ReviewFindingContentReference(hash, $"goal/review-finding-receipts/{hash}.json");
            })
            .Distinct()
            .OrderBy(reference => reference.Sha256, StringComparer.Ordinal)
            .ToArray();
        var evidenceIdentity = receiptReferences.Length == 0
            ? null
            : WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(string.Join("\n", receiptReferences.Select(reference => reference.Sha256))));
        return new CanonicalReviewFindingEntry(
            finding.StableId,
            selected.Task.RequiredRole,
            finding.State,
            finding.Severity,
            finding.Category,
            finding.Location,
            finding.Description,
            selected.Verification.ReviewedCommit,
            BuildVerdictIdentity(selected.Verification),
            evidenceIdentity,
            finding.EvidenceRequest,
            finding.EvidenceOutcome,
            selected.Round,
            receiptReferences,
            anchorProof);
    }

    private static string? DetermineFallbackReason(
        bool repairRequested,
        TaskVerificationRecord? verification,
        string? currentCandidateSha)
    {
        if (!repairRequested)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(currentCandidateSha) || string.IsNullOrWhiteSpace(verification?.ReviewedCommit))
        {
            return "current-candidate-missing";
        }
        if (!string.Equals(currentCandidateSha.Trim(), verification.ReviewedCommit.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "candidate-changed";
        }
        if ((verification.FindingEvidenceReceipts ?? []).Any(receipt =>
                !string.Equals(receipt.CandidateSha, currentCandidateSha.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return "material-evidence-changed";
        }
        return null;
    }

    private static string BuildVerdictIdentity(TaskVerificationRecord verification) =>
        WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(string.Join("\n",
            verification.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            verification.CompletionVerdictRule ?? string.Empty,
            verification.ReviewFindingContractViolation?.Code ?? string.Empty)));

    private static WorkerContextPreparationException PreparationFailure(string reason, string detail) =>
        new(new LogicalArtifactIdentity("goal/review-finding-history.json"), reason, detail);
}
