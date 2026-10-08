using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorStewardTriggerKind
{
    DeveloperNoChangeWithConfirmedRed,
    PlannerOutputContractRejected,
    AcceptanceCollectionGuardClass,
    DeveloperGateReopenNoCommit,
    DeveloperReviewerFindingNoCommit,
    ReviewerOrTesterBlockerWithAnswer
}

internal sealed record ConductorStewardTrigger(
    string GoalId,
    string TaskId,
    string CandidateSha,
    ConductorStewardTriggerKind Kind,
    DateTimeOffset OccurredAt,
    string Evidence,
    string WorkerResult,
    IReadOnlyList<string> EvidenceReferences,
    IReadOnlyList<string> AcceptanceCriteria)
{
    internal string CaseLetter => Kind switch
    {
        ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed => "A",
        ConductorStewardTriggerKind.PlannerOutputContractRejected => "B",
        ConductorStewardTriggerKind.DeveloperGateReopenNoCommit => "D",
        ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit => "E",
        ConductorStewardTriggerKind.ReviewerOrTesterBlockerWithAnswer => "F",
        _ => "C"
    };

    internal string Identity => $"{GoalId}:{TaskId}:{CandidateSha}:{Kind}";
    internal string TaskCandidateIdentity => $"{GoalId}:{TaskId}:{CandidateSha}";
}

internal sealed partial class ConductorStewardTriggerDetector(
    Func<Goal, string, string?>? candidateAddedClassCollection = null,
    Func<Goal, IReadOnlyList<string>>? acceptanceTrxPaths = null,
    ConductorStewardCaseDSources? caseDSources = null,
    ConductorStewardAnsweredBlockerDetector? answeredBlockers = null)
{
    private const string GuardName = "AcceptanceGateEngine_disabled_collections_spanning_lanes_share_an_exclusive_resource";
    private static readonly Regex OffendingClass = new(
        "Disabled-collection test class ['\"](?<class>[^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly Func<Goal, string, string?> _candidateAddedClassCollection = candidateAddedClassCollection ?? ((_, _) => null);
    private readonly Func<Goal, IReadOnlyList<string>> _acceptanceTrxPaths = acceptanceTrxPaths ?? (_ => []);

    internal IReadOnlyList<ConductorStewardTrigger> Detect(Goal goal,
        Func<ConductorStewardTriggerKind, string, string, bool>? needsInspection = null)
    {
        if (goal.IsTerminal) return [];
        var result = new List<ConductorStewardTrigger>();
        var criteria = goal.RefinedSpec?.AcceptanceCriteria.ToArray() ?? [];
        foreach (var task in goal.Tasks)
        {
            var verification = task.LastVerification;
            var dispatch = task.LastDispatch;
            if (task.Status != WorkTaskStatus.Failed || verification is null || dispatch is null)
                continue;
            var candidateSha = dispatch.BaseCommit ?? dispatch.ResultCommit;
            if (string.IsNullOrWhiteSpace(candidateSha)) continue;
            var workerResult = ExtractWorkerResult(verification.AuthoritativeStandardOutput ?? verification.StandardOutput);
            if (task.RequiredRole == AgentRole.Developer &&
                IsNoCommitRejection(verification) &&
                (needsInspection?.Invoke(ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed,
                    task.Id.Value, candidateSha) ?? true) &&
                TryFindConfirmedRed(goal, candidateSha, out var redEvidence, out var redReferences))
            {
                result.Add(new ConductorStewardTrigger(goal.Id.Value, task.Id.Value, candidateSha,
                    ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed,
                    verification.DispatchStartedAt ?? dispatch.DispatchedAt,
                    redEvidence, workerResult, redReferences, criteria));
            }
            if (task.RequiredRole == AgentRole.Planner &&
                string.Equals(verification.CompletionVerdictRule, "planner-output-contract-rejected", StringComparison.Ordinal) &&
                (needsInspection?.Invoke(ConductorStewardTriggerKind.PlannerOutputContractRejected,
                    task.Id.Value, candidateSha) ?? true))
            {
                result.Add(new ConductorStewardTrigger(goal.Id.Value, task.Id.Value, candidateSha,
                    ConductorStewardTriggerKind.PlannerOutputContractRejected,
                    verification.DispatchStartedAt ?? dispatch.DispatchedAt,
                    verification.AuthoritativeStandardError ?? verification.StandardError,
                    workerResult,
                    [$"planner-rule={verification.CompletionVerdictRule}", $"candidate-sha={candidateSha}"], criteria));
            }
        }

        if (goal.Status == GoalStatus.AcceptanceFailed && goal.LatestAcceptanceFailure is { } failure &&
            !string.IsNullOrWhiteSpace(failure.BranchHeadSha))
        {
            var developer = goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
            if (developer is not null &&
                (needsInspection?.Invoke(ConductorStewardTriggerKind.AcceptanceCollectionGuardClass,
                    developer.Id.Value, failure.BranchHeadSha!) ?? true))
            {
                var sources = (failure.CheckAttributions ?? [])
                    .Select(attribution => (Evidence: $"{attribution.CheckName}: {attribution.Evidence}", TrxPath: (string?)null))
                    .ToList();
                foreach (var trxPath in _acceptanceTrxPaths(goal))
                {
                    var trx = AcceptanceTrxFailureReader.Read(trxPath);
                    if (trx.Status != AcceptanceTrxReadStatus.Readable) continue;
                    sources.AddRange(trx.Failures.Select(item =>
                        (Evidence: $"{item.TestName}: {item.Message}", TrxPath: (string?)trxPath)));
                }
                foreach (var source in sources)
                {
                    var evidence = source.Evidence;
                    if (!evidence.Contains(GuardName, StringComparison.Ordinal)) continue;
                    var match = OffendingClass.Match(evidence);
                    if (!match.Success) continue;
                    var className = match.Groups["class"].Value;
                    var collection = _candidateAddedClassCollection(goal, className);
                    if (string.IsNullOrWhiteSpace(collection)) continue;
                    result.Add(new ConductorStewardTrigger(goal.Id.Value, developer.Id.Value,
                        failure.BranchHeadSha!, ConductorStewardTriggerKind.AcceptanceCollectionGuardClass,
                        failure.OccurredAt, $"{evidence} Collection: {collection}.",
                        ExtractWorkerResult(developer.LastVerification?.AuthoritativeStandardOutput ??
                            developer.LastVerification?.StandardOutput ?? string.Empty),
                        (source.TrxPath is null
                            ? new[] { $"acceptance-guard={GuardName}", $"offending-class={className}", $"collection={collection}" }
                            : new[] { $"acceptance-guard={GuardName}", $"offending-class={className}", $"collection={collection}", $"trx:{source.TrxPath}" }),
                        criteria));
                    break;
                }
            }
        }
        DetectCaseD(goal, needsInspection, result);
        DetectCaseE(goal, needsInspection, result);
        if (answeredBlockers is not null) result.AddRange(answeredBlockers.Detect(goal, needsInspection));
        return result;
    }

    private static bool TryFindConfirmedRed(
        Goal goal, string candidateSha, out string evidence, out IReadOnlyList<string> references)
    {
        var latest = goal.Tasks.SelectMany(task => task.VerificationHistory)
            .SelectMany(verification => (verification.FindingEvidenceReceipts ?? [])
                .Select(receipt => (verification.CompletedAt, Receipt: receipt)))
            .Where(item => string.Equals(item.Receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CompletedAt)
            .FirstOrDefault();
        if (latest.Receipt is { } receipt)
        {
            var candidate = receipt.Arms?.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
            var baseline = receipt.Arms?.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
            if (candidate is not { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red,
                    FailingTestIdentities.Count: > 0 } ||
                !string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
                baseline is { Disposition: not FindingEvidenceArmDisposition.Green })
            {
                evidence = string.Empty;
                references = [];
                return false;
            }
            var failureMessages = (candidate.TestResultPaths ?? [])
                .Select(AcceptanceTrxFailureReader.Read)
                .Where(result => result.Status == AcceptanceTrxReadStatus.Readable)
                .SelectMany(result => result.Failures)
                .Where(failure => candidate.FailingTestIdentities.Any(identity =>
                    string.Equals(identity, failure.TestName, StringComparison.Ordinal) ||
                    identity.EndsWith($".{failure.TestName}", StringComparison.Ordinal)))
                .Select(failure => $"{failure.TestName}: {failure.Message}")
                .ToArray();
            evidence = $"Confirmed candidate RED at {candidateSha}: {string.Join(", ", candidate.FailingTestIdentities)}. " +
                       $"Assertion/output: {candidate.Summary}" +
                       (failureMessages.Length == 0 ? string.Empty : $" Test messages: {string.Join(" | ", failureMessages)}");
            references = [$"finding-receipt={receipt.ReceiptId}", $"candidate-sha={candidateSha}"];
            return true;
        }
        evidence = string.Empty;
        references = [];
        return false;
    }

    private static string ExtractWorkerResult(string output)
    {
        var start = output.LastIndexOf("WORKER_RESULT:", StringComparison.Ordinal);
        if (start < 0) return output;
        var end = output.IndexOf("END_WORKER_RESULT", start, StringComparison.Ordinal);
        return end < 0 ? output[start..] : output[start..(end + "END_WORKER_RESULT".Length)];
    }
}
