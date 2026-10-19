using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DeferredNoChangeEvidenceContextProjector
{
    private const int SummaryLimit = 12;

    internal static void AddArtifacts(
        Goal goal, TaskSpec task, string? currentCandidateSha, DeferredNoChangeEvidenceContext? context,
        Action<WorkerProfileDispatcher.WorkerContextSemanticSource, string, ContextArtifactKind, byte[], IReadOnlyList<AgentRole>?, ContextDeliveryMode?> addSource)
    {
        if (task.RequiredRole is not (AgentRole.Tester or AgentRole.Reviewer) || context is null || context.Receipts.Count == 0) return;
        if (context.GoalId != goal.Id.Value || !string.Equals(context.CurrentCandidateSha, currentCandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new WorkerContextPreparationException(new("goal/deferred-no-change-evidence.json"),
                "deferred-evidence-context-mismatch", "The host evidence snapshot does not match preparation.");

        var bodies = new List<object>();
        foreach (var receipt in context.Receipts.OrderByDescending(receipt => receipt.IsCurrent)
                     .ThenByDescending(receipt => receipt.Ordinal).ThenBy(receipt => receipt.DeveloperTaskId, StringComparer.Ordinal))
        {
            string? identity = null;
            string? hash = null;
            if (receipt.BodyJson is not null && receipt.AttemptId is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(receipt.BodyJson);
                hash = WorkerContextArtifact.Hash(bytes);
                // Encode an identifier as a single path component; it is never a filesystem locator.
                identity = $"goal/deferred-no-change-receipts/{Uri.EscapeDataString(receipt.AttemptId)}-{hash}.json";
                Add(identity, bytes, receipt.IsCurrent ? ContextDeliveryMode.MandatoryFile : ContextDeliveryMode.HistoricalFile);
            }
            bodies.Add(new
            {
                receipt.AttemptId, receipt.DeveloperTaskId, receipt.CandidateSha, receipt.Ordinal,
                Currentness = receipt.IsCurrent ? "current" : "historical",
                Availability = receipt.Availability.ToString(),
                CurrentGreenProof = receipt.IsCurrent && receipt.Availability == DeferredNoChangeEvidenceAvailability.Green,
                Selections = receipt.Selections.Take(SummaryLimit), OmittedSelections = Math.Max(0, receipt.Selections.Count - SummaryLimit),
                Declared = receipt.Declared.Take(SummaryLimit), OmittedDeclarations = Math.Max(0, receipt.Declared.Count - SummaryLimit),
                NotRun = receipt.NotRun.Take(SummaryLimit), OmittedNotRun = Math.Max(0, receipt.NotRun.Count - SummaryLimit),
                receipt.CandidateArm?.ExecutedTestCount,
                NegativeControlExecuted = false,
                Diagnostic = receipt.Diagnostic is { } diagnostic ? diagnostic[..Math.Min(160, diagnostic.Length)] : null,
                LogicalIdentity = identity, Sha256 = hash
            });
        }

        var covered = context.Receipts.Where(receipt => receipt.IsCurrent && receipt.Availability == DeferredNoChangeEvidenceAvailability.Green)
            .SelectMany(receipt => receipt.Selections).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latestFindings = goal.Tasks.Where(owner => owner.RequiredRole is AgentRole.Tester or AgentRole.Reviewer)
            .SelectMany(owner => owner.VerificationHistory)
            .OrderBy(record => record.CompletedAt)
            .SelectMany(record => record.MergedReviewFindings ?? [])
            .GroupBy(finding => finding.StableId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var requirements = ReviewFindings.GetOpenBlockingFindings(latestFindings, goal.EffectiveAcceptanceCriteriaCorrections)
            .Where(finding => finding.EvidenceRequest is not null)
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .Select(finding =>
            {
                // Compare recorded selectors exactly. Expanding a different selector here could
                // incorrectly claim execution coverage that the canonical request never asserted.
                var selections = finding.EvidenceRequest!.Selections
                    .Select(selection => $"{selection.TestProject}:{selection.TestClass}").ToArray();
                return new
                {
                    finding.StableId, Selections = selections,
                    CoveredSelections = selections.Where(covered.Contains).ToArray(),
                    OutstandingSelections = selections.Where(selection => !covered.Contains(selection)).ToArray(),
                    NegativeControlRequired = finding.EvidenceRequest.NegativeControl is not null || finding.EvidenceRequest.Mutation is not null,
                    NegativeControlExecuted = false,
                    FindingStateUnchanged = true
                };
            }).ToArray();
        var coverageBytes = JsonSerializer.SerializeToUtf8Bytes(new { ContractVersion = 1, context.GoalId,
            CurrentCandidateSha = currentCandidateSha, CoveredSelections = covered.Order(StringComparer.Ordinal).ToArray(), Requirements = requirements });
        var coverageHash = WorkerContextArtifact.Hash(coverageBytes);
        var coverageIdentity = $"goal/deferred-no-change-coverage/{coverageHash}.json";
        Add(coverageIdentity, coverageBytes, ContextDeliveryMode.OnDemandFile);
        var summary = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ContractVersion = 1, context.GoalId, CurrentCandidateSha = currentCandidateSha,
            Receipts = bodies.Take(SummaryLimit), OmittedReceipts = Math.Max(0, bodies.Count - SummaryLimit),
            Coverage = new { LogicalIdentity = coverageIdentity, Sha256 = coverageHash },
            CoveredSelections = covered.Order(StringComparer.Ordinal).Take(SummaryLimit),
            OmittedCoveredSelections = Math.Max(0, covered.Count - SummaryLimit),
            OutstandingRequirements = requirements.Take(SummaryLimit).Select(requirement => new
            {
                requirement.StableId,
                CoveredSelections = requirement.CoveredSelections.Take(SummaryLimit),
                OutstandingSelections = requirement.OutstandingSelections.Take(SummaryLimit),
                OmittedSelections = Math.Max(0, requirement.CoveredSelections.Length - SummaryLimit) +
                    Math.Max(0, requirement.OutstandingSelections.Length - SummaryLimit),
                requirement.NegativeControlRequired, requirement.NegativeControlExecuted
            }),
            OmittedRequirements = Math.Max(0, requirements.Length - SummaryLimit),
            Notice = "Covered focused selections do not close findings. Additional selections and negative controls require their own execution evidence."
        });
        Add("goal/deferred-no-change-evidence.json", summary, ContextDeliveryMode.InlineFull);

        void Add(string identity, byte[] bytes, ContextDeliveryMode mode) => addSource(
            WorkerProfileDispatcher.WorkerContextSemanticSource.DeferredNoChangeEvidence,
            identity, ContextArtifactKind.RegisteredContext, bytes, [AgentRole.Tester, AgentRole.Reviewer], mode);
    }
}
