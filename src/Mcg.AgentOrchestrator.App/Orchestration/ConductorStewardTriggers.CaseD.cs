using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorStewardTriggerDetector
{
    // ConductorDriver's acceptance-failure retry feedback identifies this recovery round.
    internal const string AcceptanceRetryPrefix = "Acceptance criteria unmet; retrying task with feedback";

    // The same state predicate owns detection and apply-time admission. A case label is not proof.
    internal static bool IsCaseDTask(Goal goal, TaskSpec task, string? currentHead)
    {
        var failure = goal.RetainedAcceptanceFailure;
        var dispatch = task.LastDispatch;
        var verification = task.LastVerification;
        if (goal.IsTerminal || task != goal.Tasks.LastOrDefault(item => item.RequiredRole == AgentRole.Developer) ||
            task.RequiredRole != AgentRole.Developer || task.Status != WorkTaskStatus.Failed ||
            task.LatestRetryAt is null || task.LatestRetryInherited || dispatch is null || verification is null || failure is null ||
            string.IsNullOrWhiteSpace(currentHead) || string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            !string.Equals(currentHead, dispatch.BaseCommit, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentHead, failure.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
            verification.HasCommittedChanges ||
            (!string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
             !string.Equals(dispatch.ResultCommit, dispatch.BaseCommit, StringComparison.OrdinalIgnoreCase)))
            return false;

        var retry = goal.Timeline.LastOrDefault(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried);
        if (retry is null || !retry.Message.StartsWith(AcceptanceRetryPrefix, StringComparison.Ordinal) ||
            retry.OccurredAt < failure.OccurredAt || retry.OccurredAt < task.LatestRetryAt ||
            retry.OccurredAt > dispatch.DispatchedAt ||
            TryFindConfirmedRed(goal, currentHead, out _, out _))
            return false;

        if (IsNoCommitRejection(verification)) return true;
        var output = verification.AuthoritativeStandardOutput;
        if (!verification.WorkerResultPresent || output is null ||
            !output.Contains("WORKER_RESULT:", StringComparison.Ordinal) ||
            !output.Contains("END_WORKER_RESULT", StringComparison.Ordinal)) return false;
        var workerResult = ExtractWorkerResult(output);
        return WorkerResultBlockers.TryGetBlockersStatus(workerResult, out var blockers) &&
               blockers == WorkerResultBlockers.BlockersStatus.Present &&
               Regex.IsMatch(workerResult, @"^commit:\s*none\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }

    private void DetectCaseD(Goal goal,
        Func<ConductorStewardTriggerKind, string, string, bool>? needsInspection,
        List<ConductorStewardTrigger> result)
    {
        if (caseDSources is null) return;
        var task = goal.Tasks.LastOrDefault(item => item.RequiredRole == AgentRole.Developer);
        var head = caseDSources.ResolveHead(goal);
        if (task is null || !IsCaseDTask(goal, task, head) ||
            !(needsInspection?.Invoke(ConductorStewardTriggerKind.DeveloperGateReopenNoCommit, task.Id.Value, head!) ?? true))
            return;

        var failure = goal.RetainedAcceptanceFailure!;
        var evidence = new StringBuilder().AppendLine($"Failed checks: {string.Join(", ", failure.FailedChecks)}");
        foreach (var attribution in failure.CheckAttributions ?? [])
            evidence.AppendLine($"{attribution.CheckName}: {attribution.Evidence}");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var paths = caseDSources.TrxPaths(goal);
        if (paths.Count == 0) evidence.AppendLine("TRX unavailable for acceptance failure.");
        foreach (var path in paths)
        {
            var trx = AcceptanceTrxFailureReader.Read(path);
            evidence.AppendLine($"TRX: {path} ({trx.Status}) {trx.Detail}");
            foreach (var item in trx.Failures)
            {
                if (!string.IsNullOrWhiteSpace(item.TestName)) identities.Add(item.TestName);
                evidence.AppendLine($"Failing test: {item.TestName}: {item.Message}");
            }
        }
        evidence.AppendLine($"Apparatus genuine reason: {caseDSources.GenuineReason(goal, head!) ?? "genuine reason unavailable"}");
        evidence.AppendLine($"Candidate changed paths: {string.Join(", ", caseDSources.ChangedPaths(goal, head!))}");
        foreach (var row in caseDSources.Index.Read().Where(row => row.TestIdentity is not null && identities.Contains(row.TestIdentity)))
            evidence.AppendLine($"Index: kind={row.Kind} goalId={row.GoalId} checkName={row.CheckName} testIdentity={row.TestIdentity} " +
                $"insideChangedPaths={row.InsideChangedPaths} recordedAt={row.RecordedAt:O} candidateSha={row.CandidateSha} " +
                $"evidenceKind={row.EvidenceKind} source={row.ResolvedSourcePath} signature={row.ExceptionSignature} fingerprint={row.MessageFingerprint}");

        var workerResult = ExtractWorkerResult(task.LastVerification!.AuthoritativeStandardOutput ?? task.LastVerification.StandardOutput);
        result.Add(new ConductorStewardTrigger(goal.Id.Value, task.Id.Value, head!,
            ConductorStewardTriggerKind.DeveloperGateReopenNoCommit,
            task.LastVerification.DispatchStartedAt ?? task.LastDispatch!.DispatchedAt,
            evidence.ToString(), workerResult,
            new[] { $"acceptance-failure-head={head}", $"dispatch-base={head}", $"apparatus-genuine={head}" }
                .Concat(identities.Select(identity => $"failing-test={identity}")).ToArray(),
            goal.RefinedSpec?.AcceptanceCriteria.ToArray() ?? []));
    }
}
