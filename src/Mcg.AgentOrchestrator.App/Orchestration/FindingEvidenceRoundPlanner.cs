using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only positive, unqualified class alternatives have set semantics here. Opaque filters
// retain their executor validation path; a method or exclusion is never class coverage.
internal static class FindingEvidenceRoundPlanner
{
    private static readonly Regex ClassName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    internal static bool TryExpandClasses(string filter, out IReadOnlyList<string> classes)
    {
        classes = [];
        var depth = 0;
        foreach (var character in filter)
        {
            if (character == '(') depth++;
            else if (character == ')' && --depth < 0) return false;
        }
        if (depth != 0) return false;
        var parts = filter.Split('|', StringSplitOptions.TrimEntries);
        var names = parts.Select(part => part.Trim('(', ')').Trim())
            .Select(part => part.StartsWith("FullyQualifiedName~", StringComparison.Ordinal)
                ? part["FullyQualifiedName~".Length..] : part).ToArray();
        if (names.Length == 0 || names.Any(name => !ClassName.IsMatch(name))) return false;
        classes = names.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return true;
    }

    internal static IReadOnlyList<FindingEvidenceSelection> ExpandSelections(
        IReadOnlyList<FindingEvidenceSelection> selections) =>
        selections.SelectMany(selection => TryExpandClasses(selection.TestClass, out var classes)
                ? classes.Select(name => new FindingEvidenceSelection(selection.TestProject, name))
                : [selection])
            .Distinct().OrderBy(selection => selection.TestProject, StringComparer.Ordinal)
            .ThenBy(selection => selection.TestClass, StringComparer.Ordinal).ToArray();

    internal static bool HasClassSet(FindingEvidenceRequest request) =>
        request.Selections.Count > 0 && request.Selections.All(selection =>
            TryExpandClasses(selection.TestClass, out _));

    private static string FormatFindingEvidenceSelection(FindingEvidenceSelection selection) =>
        selection.TestProject + ":" + selection.TestClass;

    internal static IReadOnlyList<FindingEvidenceRequestDisposition> BuildInitialRequestDispositions(
        IReadOnlyList<FindingEvidenceBatch> batches,
        FindingEvidenceBatch executingBatch,
        string candidateSha,
        string findingRoundFingerprint,
        string executionBasisIdentity)
    {
        var dispositions = new List<FindingEvidenceRequestDisposition>();
        foreach (var batch in batches)
        {
            var isExecuting = ReferenceEquals(batch, executingBatch);
            var disposition = isExecuting
                ? batch.Members.Count > 1 ? "executed-batched" : "executed-standalone"
                : batch.Members.Count > 1 ? "pending-batched" : "pending-standalone";
            var reason = batch.Members.Count > 1
                ? batch.TypedRequest.Selections.Select(selection => selection.TestProject).Distinct().Count() == 1
                    ? "compatible-same-project" : "compatible-class-union"
                : ResolveStandaloneEvidenceReason(batches, batch);
            var owner = batch.Members.FirstOrDefault(member =>
                FindingEvidenceRoundPlanner.HasClassSet(member.TypedRequest) &&
                ExpandSelections(member.TypedRequest.Selections).ToHashSet()
                    .IsSupersetOf(ExpandSelections(batch.TypedRequest.Selections)));
            foreach (var member in batch.Members)
            {
                var coveredByOwner = owner is not null && !ReferenceEquals(owner, member);
                dispositions.AddRange(member.Findings.Select(finding => new FindingEvidenceRequestDisposition(
                    finding.StableId,
                    member.Identity,
                    disposition,
                    coveredByOwner ? "covered-by-pending-superset" : reason,
                    coveredByOwner ? FindingEvidenceRequestDecision.CoveredByPendingRequest
                        : FindingEvidenceRequestDecision.PendingExecution,
                    candidateSha, findingRoundFingerprint, coveredByOwner ? owner!.Identity : null,
                    member.ReusedSourceReceipts?.Select(receipt => receipt.ReceiptId).ToArray(),
                    executionBasisIdentity)));
            }
        }

        return dispositions;
    }

    private static string ResolveStandaloneEvidenceReason(
        IReadOnlyList<FindingEvidenceBatch> batches,
        FindingEvidenceBatch standaloneBatch)
    {
        foreach (var other in batches.Where(batch => !ReferenceEquals(batch, standaloneBatch)))
        {
            var reason = GetFindingEvidenceUnbatchedReason(standaloneBatch.TypedRequest, other.TypedRequest);
            if (reason is not null)
            {
                return reason;
            }
        }

        return "single-request";
    }

    internal static IReadOnlyList<FindingEvidenceBatch> BuildFindingEvidenceBatches(
        IReadOnlyList<FindingEvidenceRequestGroup> groups)
    {
        var batches = new List<FindingEvidenceBatch>();
        // An explicit union can cover requests from several projects. Independent project
        // requests keep their fail-fast order unless such a union connects them.
        var unions = groups.Where(group => HasClassSet(group.TypedRequest) &&
            group.TypedRequest.Selections.Select(selection => selection.TestProject).Distinct().Count() > 1).ToArray();
        foreach (var group in groups)
        {
            var batchIndex = batches.FindIndex(batch => CanBatch(batch.TypedRequest, group.TypedRequest, unions));
            if (batchIndex < 0)
            {
                batches.Add(new FindingEvidenceBatch(
                    group.Identity,
                    group.Request,
                    group.TypedRequest,
                    [.. group.Findings],
                    [group]));
                continue;
            }

            var batch = batches[batchIndex];
            var selections = ExpandSelections(batch.TypedRequest.Selections.Concat(group.TypedRequest.Selections).ToArray());
            var request = batch.TypedRequest with { Selections = selections };
            batches[batchIndex] = new FindingEvidenceBatch(
                FindingEvidenceExecutionClassifier.BuildRequestIdentity(request),
                string.Join("; ", selections.Select(FormatFindingEvidenceSelection)),
                request,
                [.. batch.Findings, .. group.Findings],
                [.. batch.Members, group]);
        }

        return batches;
    }

    private static bool CanBatch(FindingEvidenceRequest left, FindingEvidenceRequest right,
        IReadOnlyList<FindingEvidenceRequestGroup> unions)
    {
        if (GetFindingEvidenceUnbatchedReason(left, right) is not null) return false;
        var connected = left.Selections.Select(selection => selection.TestProject).ToHashSet(StringComparer.Ordinal);
        int priorCount;
        do
        {
            priorCount = connected.Count;
            foreach (var union in unions.Where(union =>
                         GetFindingEvidenceUnbatchedReason(left, union.TypedRequest) is null))
            {
                var projects = union.TypedRequest.Selections.Select(selection => selection.TestProject).ToArray();
                if (connected.Overlaps(projects)) connected.UnionWith(projects);
            }
        } while (connected.Count != priorCount);
        return right.Selections.All(selection => connected.Contains(selection.TestProject));
    }

    private static string? GetFindingEvidenceUnbatchedReason(
        FindingEvidenceRequest left,
        FindingEvidenceRequest right)
    {
        if (left.NegativeControl != right.NegativeControl) return "different-negative-control";
        if (!FindingEvidenceRevertPaths.SamePaths(left.RevertPaths, right.RevertPaths)) return "different-revert-paths";
        if (!FindingEvidenceMutation.Same(left.Mutation, right.Mutation)) return "different-mutation";
        return FindingEvidenceRoundPlanner.HasClassSet(left) && FindingEvidenceRoundPlanner.HasClassSet(right)
                ? null
                : "incompatible-filter-semantics";
    }

}

internal sealed record FindingEvidenceRequestGroup(
        string Identity,
        string Request,
        FindingEvidenceRequest TypedRequest,
        List<ReviewFinding> Findings,
        IReadOnlyList<FindingEvidenceReceipt>? ReusedSourceReceipts = null,
        string? CoverageDecisionReason = null);

internal sealed record FindingEvidenceBatch(
        string Identity,
        string Request,
        FindingEvidenceRequest TypedRequest,
        List<ReviewFinding> Findings,
        IReadOnlyList<FindingEvidenceRequestGroup> Members);
