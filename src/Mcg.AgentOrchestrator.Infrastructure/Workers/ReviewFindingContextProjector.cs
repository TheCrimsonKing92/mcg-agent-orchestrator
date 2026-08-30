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
    ReviewFindingHistoryProjectionMetrics Metrics,
    IReadOnlySet<string> ActiveBodyHashes);

internal static class ReviewFindingContextProjector
{
    private sealed record RoundSource(TaskSpec Task, TaskVerificationRecord Verification, int HistoryIndex);
    private sealed record FindingSource(TaskSpec Task, TaskVerificationRecord Verification, ReviewFinding Finding, ReviewFindingContentReference Round, int HistoryIndex);
    private sealed record CanonicalEntryProjection(CanonicalReviewFindingEntry Entry, string? FallbackReason);

    public static void AddArtifacts(
        ReviewFindingContextProjection projection,
        Action<WorkerProfileDispatcher.WorkerContextSemanticSource, string, ContextArtifactKind, byte[], IReadOnlyList<AgentRole>?, ContextDeliveryMode?> addSource)
    {
        addSource(WorkerProfileDispatcher.WorkerContextSemanticSource.ReviewFindingHistory, "goal/review-finding-history.json", ContextArtifactKind.AcceptanceCriteria, projection.LedgerBytes, null, ContextDeliveryMode.InlineFull);
        foreach (var body in projection.RoundBodies.Concat(projection.ReceiptBodies))
        {
            var mode = projection.ActiveBodyHashes.Contains(body.Sha256)
                ? ContextDeliveryMode.OnDemandFile
                : ContextDeliveryMode.HistoricalFile;
            addSource(WorkerProfileDispatcher.WorkerContextSemanticSource.ReviewFindingHistory, body.LogicalIdentity, ContextArtifactKind.RegisteredContext, body.Bytes, null, mode);
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
            artifact.Identity.Value != "context/AGENTS.md" &&
            !artifact.Identity.Value.StartsWith("goal/review-finding-rounds/", StringComparison.Ordinal) &&
            !artifact.Identity.Value.StartsWith("goal/review-finding-receipts/", StringComparison.Ordinal));

    public static ReviewFindingContextProjection Project(
        Goal goal,
        TaskSpec targetTask,
        string? currentCandidateSha,
        string? comparisonBaseSha = null,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection>? effectiveOperatorCorrections = null)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(targetTask);
        var rounds = goal.Tasks
            .SelectMany(task => task.VerificationHistory.Select((verification, historyIndex) => new RoundSource(task, verification, historyIndex)))
            .Where(source => (source.Verification.MergedReviewFindings?.Count ?? 0) > 0 ||
                              (source.Verification.FindingEvidenceReceipts?.Count ?? 0) > 0)
            .OrderBy(source => source.Verification.CompletedAt)
            .ThenBy(source => source.Task.Id.Value, StringComparer.Ordinal)
            .ThenBy(source => source.HistoryIndex)
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
                new FindingSource(source.Task, source.Verification, finding, roundReferenceBySource[source], source.HistoryIndex))).ToArray();
        var entryProjections = findingSources
            .GroupBy(source => source.Finding.StableId, StringComparer.Ordinal)
            .Select(BuildCanonicalEntry)
            .ToArray();
        var canonicalEntries = entryProjections
            .Select(projection => projection.Entry)
            .OrderBy(entry => entry.StableId, StringComparer.Ordinal)
            .ToArray();
        var entries = canonicalEntries
            .Where(entry => entry.State == ReviewFindingState.Open || entry.ResolutionProof is null)
            .ToArray();
        var convergenceEntries = canonicalEntries
            .Where(entry =>
                entry.State == ReviewFindingState.Resolved &&
                entry.Severity == FindingSeverity.Blocking &&
                entry.ResolutionProof is { Kind: "candidate-bound-evidence-receipt", ReceiptBody: not null } &&
                !string.IsNullOrWhiteSpace(currentCandidateSha) &&
                string.Equals(entry.CandidateSha, currentCandidateSha, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var earlyConvergenceEligible = convergenceEntries.Length > 0 &&
            !entries.Any(entry => entry.Severity == FindingSeverity.Blocking);
        var convergenceReceiptHashes = earlyConvergenceEligible
            ? convergenceEntries.Select(entry => entry.ResolutionProof!.ReceiptBody!.Sha256)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(hash => hash, StringComparer.Ordinal)
                .ToArray()
            : [];
        var activeRoundHashes = entries.Select(entry => entry.Round.Sha256)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var activeReceiptHashes = entries.SelectMany(entry => entry.ReceiptBodies)
            .Select(reference => reference.Sha256)
            .Concat(convergenceReceiptHashes)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var activeRoundIndex = roundIndex
            .Where(entry => activeRoundHashes.Contains(entry.Body.Sha256))
            .GroupBy(entry => entry.Body.Sha256, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(entry => entry.CompletedAt)
            .ThenBy(entry => entry.TaskId, StringComparer.Ordinal)
            .ToArray();
        var activeReceiptReferences = receiptBodies.Values
            .Where(body => activeReceiptHashes.Contains(body.Sha256))
            .OrderBy(body => body.Sha256, StringComparer.Ordinal)
            .Select(body => new ReviewFindingContentReference(body.Sha256, body.LogicalIdentity))
            .ToArray();

        var latestVerification = targetTask.VerificationHistory.LastOrDefault();
        var repairCheckpoint = targetTask.PendingReviewFindingRepairCheckpoint;
        var repairRequested = targetTask.PendingRetryRoundKind == RetryRoundKind.Mechanical &&
                              latestVerification?.ReviewFindingContractViolation is not null;
        var fallbackReason = entryProjections
            .Select(projection => projection.FallbackReason)
            .FirstOrDefault(reason => reason is not null)
            ?? DetermineFallbackReason(
                repairRequested,
                repairCheckpoint,
                latestVerification,
                currentCandidateSha);
        var mode = repairRequested && fallbackReason is null
            ? ReviewFindingHistoryProjectionMode.ContractRepair
            : ReviewFindingHistoryProjectionMode.FullInspection;
        var metrics = new ReviewFindingHistoryProjectionMetrics(
            mode,
            activeRoundIndex.Length,
            rounds.Length - activeRoundIndex.Length,
            activeReceiptReferences.Length,
            totalReceiptCount - activeReceiptReferences.Length,
            fallbackReason,
            earlyConvergenceEligible,
            earlyConvergenceEligible ? currentCandidateSha?.Trim() : null,
            convergenceReceiptHashes);
        var currentOperatorCorrections = (effectiveOperatorCorrections ?? [])
            .GroupBy(correction => correction.SupersededCriterion, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(correction => correction.RecordedAt).First())
            .OrderByDescending(correction => correction.RecordedAt)
            .ToArray();
        var ledger = new ReviewFindingHistoryLedger(
            ContextContractVersion.V1.Value,
            mode,
            fallbackReason,
            entries,
            activeRoundIndex,
            activeReceiptReferences,
            currentCandidateSha?.Trim(),
            comparisonBaseSha?.Trim(),
            currentOperatorCorrections,
            earlyConvergenceEligible);
        var ledgerBytes = JsonSerializer.SerializeToUtf8Bytes(ledger);

        byte[]? envelopeBytes = null;
        if (mode == ReviewFindingHistoryProjectionMode.ContractRepair)
        {
            var verification = latestVerification!;
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
            metrics,
            activeRoundHashes.Concat(activeReceiptHashes).ToHashSet(StringComparer.Ordinal));
    }

    private static CanonicalEntryProjection BuildCanonicalEntry(IGrouping<string, FindingSource> group)
    {
        var latestAt = group.Max(source => source.Verification.CompletedAt);
        var latest = group.Where(source => source.Verification.CompletedAt == latestAt).ToArray();
        var distinctLatest = latest
            .Select(source => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(source.Finding)))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var conflictingCandidates = latest
            .Select(source => source.Verification.ReviewedCommit ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();
        if (distinctLatest != 1 || conflictingCandidates)
        {
            throw PreparationFailure("stable-identity-ambiguous", $"Stable finding '{group.Key}' has contradictory latest states at the same timestamp.");
        }

        var selected = latest
            .OrderBy(source => source.Task.Id.Value, StringComparer.Ordinal)
            .ThenBy(source => source.HistoryIndex)
            .First();
        if (selected.Finding.State == ReviewFindingState.Resolved)
        {
            selected = FindResolvingSource(group);
        }
        var finding = selected.Finding;
        var selectedReceipts = selected.Verification.FindingEvidenceReceipts ?? [];
        var receiptReferences = selectedReceipts
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
        var anchorProof = finding.State == ReviewFindingState.Resolved
            ? (selected.Verification.ReviewFindingTouchedAnchors ?? [])
                .Where(anchor => anchor.SameAnchor(finding.Location))
                .ToArray()
            : [];
        ReviewFindingResolutionProof? resolutionProof = null;
        string? fallbackReason = null;
        if (finding.State == ReviewFindingState.Resolved)
        {
            if (anchorProof.Length > 0)
            {
                resolutionProof = new ReviewFindingResolutionProof(
                    "touched-anchor",
                    selected.Verification.ReviewedCommit);
            }
            else
            {
                var evidenceReceipt = FindCandidateBoundEvidenceReceipt(
                    finding,
                    selected.Verification.ReviewedCommit,
                    selectedReceipts);
                if (evidenceReceipt is not null)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(evidenceReceipt);
                    var hash = WorkerContextArtifact.Hash(bytes);
                    resolutionProof = new ReviewFindingResolutionProof(
                        "candidate-bound-evidence-receipt",
                        selected.Verification.ReviewedCommit,
                        new ReviewFindingContentReference(hash, $"goal/review-finding-receipts/{hash}.json"));
                }
                else if (finding.Severity == FindingSeverity.Advisory &&
                         !string.IsNullOrWhiteSpace(selected.Verification.ReviewedCommit))
                {
                    resolutionProof = new ReviewFindingResolutionProof(
                        "advisory-disposition",
                        selected.Verification.ReviewedCommit);
                }
                else
                {
                    fallbackReason = "resolved-anchor-proof-missing";
                }
            }
        }

        var evidenceIdentity = receiptReferences.Length == 0
            ? null
            : WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(string.Join("\n", receiptReferences.Select(reference => reference.Sha256))));
        return new CanonicalEntryProjection(
            new CanonicalReviewFindingEntry(
                finding.StableId,
                selected.Task.RequiredRole,
                finding.State,
                finding.Severity,
                finding.Category,
                finding.Location,
                finding.State == ReviewFindingState.Resolved && resolutionProof is not null
                    ? null
                    : finding.Description,
                selected.Verification.ReviewedCommit,
                BuildVerdictIdentity(selected.Verification),
                evidenceIdentity,
                finding.EvidenceRequest,
                finding.EvidenceOutcome,
                selected.Round,
                receiptReferences,
                resolutionProof,
                anchorProof),
            fallbackReason);
    }

    private static FindingEvidenceReceipt? FindCandidateBoundEvidenceReceipt(
        ReviewFinding finding,
        string? candidateSha,
        IReadOnlyList<FindingEvidenceReceipt> receipts)
    {
        if (string.IsNullOrWhiteSpace(candidateSha) ||
            finding.EvidenceOutcome is not
            {
                Honoured: true,
                ReceiptId.Length: > 0,
                ResultReason: FindingEvidenceOutcomeReason.ValidEvidence
            } outcome)
        {
            return null;
        }

        return receipts.FirstOrDefault(receipt =>
            string.Equals(receipt.ReceiptId, outcome.ReceiptId, StringComparison.Ordinal) &&
            string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
            receipt.Accepted &&
            receipt.Passed);
    }

    private static string? DetermineFallbackReason(
        bool repairRequested,
        ReviewFindingRepairCheckpoint? checkpoint,
        TaskVerificationRecord? verification,
        string? currentCandidateSha)
    {
        if (!repairRequested)
        {
            return null;
        }
        if (checkpoint is null || verification?.ReviewFindingContractViolation is null)
        {
            return "repair-checkpoint-missing";
        }
        if (string.IsNullOrWhiteSpace(currentCandidateSha) || string.IsNullOrWhiteSpace(checkpoint.CandidateSha))
        {
            return "current-candidate-missing";
        }
        if (!string.Equals(currentCandidateSha.Trim(), checkpoint.CandidateSha.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "candidate-changed";
        }
        var violationHash = WorkerContextArtifact.Hash(JsonSerializer.SerializeToUtf8Bytes(verification.ReviewFindingContractViolation));
        if (!string.Equals(violationHash, checkpoint.ContractViolationHash, StringComparison.Ordinal))
        {
            return "contract-violation-changed";
        }
        var evidenceHashes = (verification.FindingEvidenceReceipts ?? [])
            .Select(receipt => WorkerContextArtifact.Hash(JsonSerializer.SerializeToUtf8Bytes(receipt)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToArray();
        if (!evidenceHashes.SequenceEqual(checkpoint.EvidenceContentHashes, StringComparer.Ordinal))
        {
            return "material-evidence-changed";
        }
        var stableIds = (verification.MergedReviewFindings ?? [])
            .Select(finding => finding.StableId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(stableId => stableId, StringComparer.Ordinal)
            .ToArray();
        if (!stableIds.SequenceEqual(checkpoint.StableFindingIds, StringComparer.Ordinal))
        {
            return "stable-identities-changed";
        }
        return null;
    }

    private static FindingSource FindResolvingSource(IEnumerable<FindingSource> sources)
    {
        FindingSource? resolving = null;
        foreach (var source in sources
                     .OrderBy(item => item.Verification.CompletedAt)
                     .ThenBy(item => item.Task.Id.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.HistoryIndex))
        {
            if (source.Finding.State == ReviewFindingState.Open)
            {
                resolving = null;
            }
            else if (resolving is null)
            {
                resolving = source;
            }
        }

        return resolving ?? throw PreparationFailure(
            "stable-identity-ambiguous",
            $"Resolved stable finding '{sources.First().Finding.StableId}' has no resolving round.");
    }

    private static string BuildVerdictIdentity(TaskVerificationRecord verification) =>
        WorkerContextArtifact.Hash(Encoding.UTF8.GetBytes(string.Join("\n",
            verification.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            verification.CompletionVerdictRule ?? string.Empty,
            verification.ReviewFindingContractViolation?.Code ?? string.Empty)));

    private static WorkerContextPreparationException PreparationFailure(string reason, string detail) =>
        new(new LogicalArtifactIdentity("goal/review-finding-history.json"), reason, detail);
}
