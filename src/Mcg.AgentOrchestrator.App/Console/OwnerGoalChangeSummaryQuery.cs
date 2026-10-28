using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Preserve the applicability basis even when no acceptance event matches it.
internal sealed record OwnerGoalAcceptanceEvidence(string MainRevision, GoalOperationJournalEntry? Outcome);

// Selected-goal composition only. No worker polling, provider calls, writes, or peer snapshots.
internal sealed class OwnerGoalChangeSummaryQuery(
    Func<string, CancellationToken, Task<GoalPromotionFact?>>? promotion = null,
    Func<string, string, CancellationToken, Task<CommittedGoalChanges>>? changes = null,
    Func<TaskVerificationRecord, CancellationToken, Task<string?>>? artifact = null,
    Func<GoalId, string, GoalPromotionFact?, CancellationToken, Task<OwnerGoalAcceptanceEvidence>>? acceptance = null)
{
    internal const int MaxArtifactBytes = 1_056_000;
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _diffRead = new(1, 1);
    private (string Base, string Candidate, Task<CommittedGoalChanges> Read)? _committed;

    internal static OwnerGoalChangeSummaryQuery ForWorkspace(OrchestratorWorkspace workspace) => new(
        async (id, token) =>
        {
            using var session = new SqliteReadSession(workspace.SqliteStatePath);
            return (await SqliteOrchestratorStateRepository.ReadEpicMetadataAsync(session.Connection,
                [id], false, token)).SingleOrDefault()?.PromotionFact;
        },
        (basis, candidate, token) => Task.Run(() => GoalChangesReader.ReadCommittedRange(
            workspace.RootDirectory, basis, candidate), token),
        acceptance: (id, candidate, landed, token) => Task.Run(async () =>
        {
            // A retained promotion supplies the historical gate's main even after cleanup.
            // Otherwise resolve only the selected goal's integration ref, never its dirty worktree.
            var main = landed?.CandidateRevision == candidate ? landed.PriorMainRevision : null;
            if (main is null)
            {
                var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify",
                    $"refs/heads/{workspace.IntegrationBranch}");
                if (!result.Succeeded) throw new IOException("Integration revision unavailable.");
                main = result.Output.Trim();
            }
            token.ThrowIfCancellationRequested();
            if (!FullRevision(main)) throw new InvalidDataException("Integration revision unavailable.");
            return new OwnerGoalAcceptanceEvidence(main, await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(
                workspace.RunEventStorePath, id, candidate, main, token));
        }, token));

    internal async Task<OwnerGoalChangeSummary> LoadAsync(Goal goal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GoalPromotionFact? landed = null;
        string? promotionFailure = promotion is null ? "promotion query unavailable" : null;
        try { if (promotion is not null) landed = await promotion(goal.Id.Value, token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { promotionFailure = ex.GetType().Name; }
        if (landed is not null && landed.GoalId != goal.Id.Value)
        { landed = null; promotionFailure = "mismatched goal identity"; }

        var dispatches = goal.Tasks.SelectMany(task => task.DispatchHistory)
            .Where(item => FullRevision(item.ResultCommit)).OrderByDescending(item => item.DispatchedAt).ToArray();
        var dispatch = dispatches.FirstOrDefault();
        var candidate = landed is not null && (dispatch is null || dispatch.DispatchedAt <= landed.PromotedAt)
            ? landed.CandidateRevision : dispatch?.ResultCommit;
        candidate ??= FullRevision(goal.MetadataResultCommit) ? goal.MetadataResultCommit : null;
        // Read-only completions also record HEAD. Keep their current candidate identity, but
        // prefer a contributing range ending at that candidate over a later HEAD..HEAD range.
        var contributing = dispatches.FirstOrDefault(item =>
            string.Equals(item.ResultCommit, candidate, StringComparison.OrdinalIgnoreCase) && FullRevision(item.BaseCommit) &&
            !string.Equals(item.BaseCommit, candidate, StringComparison.OrdinalIgnoreCase));
        var promotionRange = landed is not null && landed.CandidateRevision == candidate;
        var basis = promotionRange ? landed!.PriorMainRevision : contributing?.BaseCommit ?? dispatch?.BaseCommit;
        var planned = await PlannedAsync(goal, token);
        var observed = new OwnerGoalSummarySection("unavailable — no committed comparison", [], "recorded dispatch/promotion identities");
        var previewChanges = new OwnerGoalPreviewChanges(OwnerGoalPreviewChangeKind.None, [], 0);
        if (candidate is not null && FullRevision(basis) && changes is not null)
        {
            try
            {
                Task<CommittedGoalChanges> read;
                lock (_cacheGate)
                {
                    if (_committed is not { } cached || cached.Base != basis || cached.Candidate != candidate ||
                        cached.Read.IsCanceled || cached.Read.IsFaulted)
                        _committed = (basis!, candidate, ReadChangesAsync(basis!, candidate, token));
                    read = _committed.Value.Read;
                }
                var facts = await read.WaitAsync(token);
                previewChanges = new(!facts.Available ? OwnerGoalPreviewChangeKind.Unavailable :
                    facts.TotalFiles == 0 ? OwnerGoalPreviewChangeKind.None : OwnerGoalPreviewChangeKind.Listed,
                    facts.Files, facts.TotalFiles);
                observed = facts.Available
                    ? new(facts.TotalFiles == 0 ? "observed empty committed range" : $"observed {facts.TotalFiles} files (file scope: {FileAreas(facts.Files)})",
                        facts.Files.Take(20).Select(BoundItem).ToArray(),
                        $"Git {basis}..{candidate}; " + (promotionRange ? "promotion range" : "latest contributing dispatch range, not whole-goal coverage"),
                        Math.Max(0, facts.TotalFiles - 20), facts.Files.Take(20).Where(item => item.Length > 360).ToArray())
                    : new("unavailable — " + facts.Reason, [], $"Git {basis}..{candidate}");
                // Retry an unavailable observation on the next refresh; never cache it as an empty diff.
                if (!facts.Available) lock (_cacheGate) { if (_committed?.Read == read) _committed = null; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                previewChanges = new(OwnerGoalPreviewChangeKind.Unavailable, [], 0);
                observed = new("unavailable — " + ex.GetType().Name, [], $"Git {basis}..{candidate}");
                lock (_cacheGate) _committed = null;
            }
        }
        var reported = await ReportedAsync(goal, candidate, token);
        var landing = landed is null
            ? promotionFailure is null ? "landing not recorded" : "landing unavailable — " + promotionFailure
            : $"landed candidate {landed.CandidateRevision} at {landed.PromotedAt:yyyy-MM-dd HH:mm} UTC; main {landed.PromotedMainRevision}; {landed.Provenance}";
        var (verified, previewEvidence) = await VerifiedAsync(goal, candidate, landed, token);
        return new(goal.Id.Value, goal.AuthoritativeBrief.Version, EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal),
            candidate, planned, observed, reported, verified, landing)
        {
            Preview = new(OwnerGoalTitle.Full(goal.Objective), previewChanges, previewEvidence)
        };
    }

    private async Task<CommittedGoalChanges> ReadChangesAsync(string basis, string candidate, CancellationToken token)
    {
        await _diffRead.WaitAsync(token).ConfigureAwait(false);
        try { return await changes!(basis, candidate, token).ConfigureAwait(false); }
        finally { _diffRead.Release(); }
    }

    private async Task<OwnerGoalSummarySection> PlannedAsync(Goal goal, CancellationToken token)
    {
        var task = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Planner)
            .OrderByDescending(task => task.LastDispatch?.DispatchedAt).FirstOrDefault();
        var reason = task is null || task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned or WorkTaskStatus.Running
            ? "planning pending" : "planning unavailable";
        var verification = task?.LastVerification;
        var dispatch = task?.LastDispatch;
        if (task?.Status == WorkTaskStatus.Completed && verification?.Succeeded == true && dispatch is not null)
        {
            if (dispatch.BriefVersion != goal.AuthoritativeBrief.Version || goal.RefinedSpec is not { } spec ||
                goal.AuthoritativeRefinedSpecVersion?.BriefVersion != goal.AuthoritativeBrief.Version ||
                !EffectiveAcceptanceCriteriaVersion.IsCapturedHashCurrent(spec, goal.EffectiveAcceptanceCriteriaCorrections,
                    verification.AcceptanceCriteriaVersionHash) ||
                verification.DispatchStartedAt != dispatch.DispatchedAt)
                reason = "planning stale — brief/criteria/dispatch association unavailable";
            else
            {
                try
                {
                    var text = await ReadArtifactAsync(verification, token);
                    if (text is not null && PlannerOutputContract.TryExtractDurablePlan(text, out var plan, out _) &&
                        PlannerOutputContract.SplitRequiredSections(plan).TryGetValue("acceptance criterion mapping", out var body))
                    {
                        var mapping = PlannerOutputContract.SelectCriterionMappings(body);
                        if (mapping.Count == spec.AcceptanceCriteria.Count &&
                            Enumerable.Range(1, spec.AcceptanceCriteria.Count).All(mapping.ContainsKey))
                        {
                            var bullets = mapping.OrderBy(pair => pair.Key).Select(pair =>
                                (Number: pair.Key, Plan: PlanField(pair.Value))).ToArray();
                            if (bullets.All(item => !string.IsNullOrWhiteSpace(item.Plan)))
                            {
                                var items = bullets.GroupBy(item => item.Plan, StringComparer.OrdinalIgnoreCase)
                                    .Select(group => group.Key! + " [criteria " + string.Join(",", group.Select(item => item.Number)) + "]").ToArray();
                                return new("completed plan", items.Take(5).Select(BoundItem).ToArray(),
                                    Source(verification, dispatch) + $"; brief v{dispatch.BriefVersion}; criteria {verification.AcceptanceCriteriaVersionHash}",
                                    Math.Max(0, items.Length - 5), items.Take(5).Where(item => item.Length > 360).ToArray());
                            }
                        }
                        reason = "planning stale — criterion mapping incomplete or incompatible";
                    }
                    else reason = "planning unavailable — missing, oversized or unreadable durable artifact";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { reason = "planning unavailable — " + ex.GetType().Name; }
            }
        }
        var expectations = goal.RefinedSpec is { } current &&
            goal.AuthoritativeRefinedSpecVersion?.BriefVersion == goal.AuthoritativeBrief.Version
            ? EffectiveAcceptanceCriteriaVersion.BuildSnapshot(current, goal.EffectiveAcceptanceCriteriaCorrections)
            : goal.AuthoritativeBrief.Text.Replace("\r", "").Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                // Use the same generated-heading detection as the title, only on the brief's heading.
                .Select((line, index) => index == 0 && !line.TrimStart().StartsWith('#')
                    ? OwnerGoalTitle.From(goal.AuthoritativeBrief.Text) : line)
                .Where(line => !line.TrimStart().StartsWith('#')).Take(6).ToArray();
        return new(reason + "; Expected from brief", expectations.Take(5).Select(BoundItem).ToArray(),
            $"authoritative brief v{goal.AuthoritativeBrief.Version}" +
            (goal.AuthoritativeRefinedSpecVersion is { } version ? $"; spec v{version.Version}" : ""),
            Math.Max(0, expectations.Count - 5), expectations.Take(5).Where(item => item.Length > 360).ToArray());
    }

    private async Task<OwnerGoalSummarySection> ReportedAsync(Goal goal, string? candidate, CancellationToken token)
    {
        var task = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer &&
                candidate is not null && string.Equals(task.LastDispatch?.ResultCommit, candidate, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(task => task.LastDispatch!.DispatchedAt).FirstOrDefault();
        if (task?.LastVerification is not { } record || task.LastDispatch is not { } dispatch ||
            record.DispatchStartedAt != dispatch.DispatchedAt)
            return new("unavailable — no candidate-bound structured implementation summary", [], "worker result");
        try
        {
            var text = await ReadArtifactAsync(record, token);
            if (text is not null && WorkerResultParser.TryParseFields(text, out var fields, out _) &&
                fields.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary) &&
                fields.TryGetValue("commit", out var commit) &&
                (string.Equals(commit, candidate, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(commit, "none", StringComparison.OrdinalIgnoreCase)))
                return new("worker-reported; not proof of behavior", [BoundItem(summary)], Source(record, dispatch) + "; candidate " + candidate,
                    ExpandedItems: summary.Length > 360 ? [summary] : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new("unavailable — " + ex.GetType().Name, [], Source(record, dispatch)); }
        return new("unavailable — structured summary absent or association unknown", [], Source(record, dispatch));
    }

    private async Task<(OwnerGoalSummarySection Section, OwnerGoalPreviewEvidence? Preview)> VerifiedAsync(Goal goal, string? candidate,
        GoalPromotionFact? landed, CancellationToken token)
    {
        GoalOperationJournalEntry? outcome = null;
        string? expectedMain = null;
        string? unavailable = null;
        try
        {
            if (candidate is not null && acceptance is not null)
            {
                var evidence = await acceptance(goal.Id, candidate, landed, token).WaitAsync(token);
                if (!FullRevision(evidence.MainRevision)) throw new InvalidDataException("Integration revision unavailable.");
                expectedMain = evidence.MainRevision;
                outcome = evidence.Outcome;
            }
            if (outcome is not null && (outcome.GoalId != goal.Id ||
                !outcome.HasCandidate(candidate!, expectedMain!)))
            { outcome = null; unavailable = "mismatched acceptance identity"; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { unavailable = ex.GetType().Name; }
        var obligations = goal.CriterionEvidenceObligations.Where(item =>
            item.CriterionVersion == goal.AuthoritativeRefinedSpecVersion?.Version && item.State != CriterionEvidenceState.Repaired).ToArray();
        var applicable = obligations.Where(item => item.CandidateSha == candidate && candidate is not null &&
            (item.State == CriterionEvidenceState.Failed || item.HasSatisfiedEvidenceFor(candidate)) &&
            (item.ExpectedCandidateSha is null || item.ExpectedCandidateSha == candidate)).ToArray();
        var lines = applicable.Select(item => $"{item.DisplayLabel}: {item.State}; {item.RequiredScope}; receipt {item.ReceiptId}").Take(10).ToList();
        var failed = applicable.Any(item => item.State == CriterionEvidenceState.Failed);
        var all = obligations.Length > 0 && obligations.All(item => item.HasSatisfiedEvidenceFor(candidate));
        var state = candidate is null ? "unavailable — candidate unknown" : failed ? "failed — applicable criterion receipt" :
            all ? "passed — applicable criterion receipts" : "pending — changed candidate lacks complete applicable receipts";
        OwnerGoalPreviewEvidence? previewEvidence = null;
        if (outcome is not null)
        {
            var passed = outcome.Status == GoalOperationStatus.Completed &&
                (string.Equals(outcome.AcceptanceOutcome, "passed", StringComparison.OrdinalIgnoreCase) ||
                 GoalOperationJournalGatePassedOutcome.Matches(outcome));
            if (passed || outcome.Status == GoalOperationStatus.Failed || outcome.AcceptanceOutcome == "failed")
                previewEvidence = new(OwnerGoalPreviewEvidenceKind.Gate, passed, outcome.At,
                    passed ? null : outcome.FailedCheckNames?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
            state = passed
                ? failed ? state : obligations.Length == 0 || all ? "passed — applicable full acceptance" :
                    "pending — full acceptance passed; criterion evidence outstanding"
                : failed ? state : outcome.Status == GoalOperationStatus.Failed || outcome.AcceptanceOutcome == "failed"
                    ? "failed — applicable full acceptance" : "pending — acceptance " + outcome.AcceptanceOutcome;
            lines.Add(BoundItem($"Full acceptance: {outcome.AcceptanceOutcome}; event {outcome.IdempotencyKey}; main {outcome.MainHeadSha}; {outcome.At:O}"));
            lines.AddRange((outcome.FailedCheckNames ?? []).Take(5).Select(BoundItem));
        }
        if (unavailable is not null) lines.Add("Acceptance evidence unavailable — " + unavailable);
        if (candidate is not null && goal.RetainedAcceptanceFailure is { } failure &&
            string.Equals(failure.BranchHeadSha, candidate, StringComparison.OrdinalIgnoreCase) &&
            (outcome is null || failure.OccurredAt > outcome.At))
        {
            if (expectedMain is not null && string.Equals(failure.MainHeadSha, expectedMain, StringComparison.OrdinalIgnoreCase))
            {
                previewEvidence = new(OwnerGoalPreviewEvidenceKind.Gate, false, failure.OccurredAt,
                    failure.FailedChecks.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
                state = "failed — recorded acceptance (main " + failure.MainHeadSha + ")";
                lines.AddRange(failure.FailedChecks.Take(5).Select(BoundItem));
            }
            else
                lines.Add("Retained acceptance failure " + (expectedMain is null ? "applicability unavailable" : "superseded") +
                    "; recorded main " + (failure.MainHeadSha ?? "unknown") + "; expected main " + (expectedMain ?? "unavailable"));
        }
        if (obligations.Any(item => item.Owner == CriterionEvidenceOwner.Operator && !item.HasSatisfiedEvidenceFor(candidate)))
            lines.Add("Operator obligation outstanding");
        if (candidate is not null)
            foreach (var task in goal.Tasks)
                foreach (var receipt in task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
                             .GroupBy(item => item.ReceiptId).Select(group => group.Last())
                             .Where(item => item.Accepted && item.CandidateSha == candidate).Take(5))
                    lines.Add($"Focused execution: {(FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(task, candidate, [receipt.ReceiptId]) ? "passed" : "failed")}; receipt {receipt.ReceiptId}; full acceptance tracked separately");
        if (previewEvidence is null && candidate is not null)
        {
            var focused = goal.Tasks.SelectMany(task => task.VerificationHistory.SelectMany(record =>
                    (record.FindingEvidenceReceipts ?? []).Select(receipt => (Task: task, Record: record, Receipt: receipt))))
                .Where(item => item.Receipt.Accepted &&
                    string.Equals(item.Receipt.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Record.CompletedAt).FirstOrDefault();
            if (focused.Receipt is not null)
                previewEvidence = new(OwnerGoalPreviewEvidenceKind.Focused,
                    FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(focused.Task, candidate, [focused.Receipt.ReceiptId]),
                    focused.Record.CompletedAt);
        }
        return (new(state, lines.Take(20).ToArray(), "criterion obligations/acceptance run events/retained failure; candidate " + (candidate ?? "unknown"),
            Math.Max(0, applicable.Length - 10) + Math.Max(0, lines.Count - 20)), previewEvidence);
    }

    private async Task<string?> ReadArtifactAsync(TaskVerificationRecord record, CancellationToken token)
    {
        if (artifact is not null)
        {
            var text = await artifact(record, token).WaitAsync(token);
            return text is not null && Encoding.UTF8.GetByteCount(text) <= MaxArtifactBytes ? text : null;
        }
        if (record.AuthoritativeStandardOutput is { } output && Encoding.UTF8.GetByteCount(output) <= MaxArtifactBytes &&
            (record.StandardOutputPath is null || output.Contains(PlannerOutputContract.DurablePlanBeginMarker, StringComparison.Ordinal) || output.Contains("WORKER_RESULT:", StringComparison.Ordinal)))
            return output;
        if (record.StandardOutputPath is not { } path) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxArtifactBytes) return null;
        var bytes = new byte[MaxArtifactBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        return length > MaxArtifactBytes ? null : new UTF8Encoding(false, true).GetString(bytes, 0, length);
    }

    private static string Source(TaskVerificationRecord record, TaskDispatchRecord dispatch) =>
        (record.StandardOutputPath ?? "retained structured worker artifact") + $"; dispatch {dispatch.DispatchedAt:O}";

    private static string? PlanField(string mapping)
    {
        var match = Regex.Match(mapping, @"(?:^|;)\s*plan\s*=\s*(?<plan>.*?)(?=;\s*[\w_-]+\s*=|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["plan"].Value.Trim() : null;
    }

    private static bool FullRevision(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static string FileAreas(IReadOnlyList<string> files)
    {
        var areas = files.Take(100).Select(file =>
        {
            var path = file.Length > 2 ? file[2..].Replace('\\', '/') : file;
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "repository root" : path[..slash];
        }).Distinct(StringComparer.Ordinal).ToArray();
        return string.Join(", ", areas.Take(5).Select(BoundItem)) + (areas.Length > 5 ? "; more areas omitted" : "");
    }
    private static string BoundItem(string text)
    {
        var plain = text.Replace('\r', ' ').Replace('\n', ' ').Replace('{', ' ').Replace('}', ' ').Trim();
        if (plain.Length <= 360) return plain;
        var end = StringInfo.ParseCombiningCharacters(plain).TakeWhile(index => index <= 359).LastOrDefault();
        return plain[..end] + "… [full text in cited source / expanded text below]";
    }
}
