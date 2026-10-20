using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    // Read-only: no observation, reconciliation, directory discovery, or scheduling at dispatch.
    internal DeferredNoChangeEvidenceContext CaptureDeferredEvidenceContext(Goal goal, string? currentCandidateSha) =>
        CaptureDeferredEvidenceContext(goal, currentCandidateSha, null);

    internal DeferredNoChangeEvidenceContext CaptureDeferredEvidenceContext(Goal goal, string? currentCandidateSha, Action? validatedForTests)
    {
        var captured = new List<(DeferredNoChangeEvidenceSnapshot Receipt, int TimelineIndex)>();
        foreach (var developer in goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer))
        {
            var outcomes = developer.VerificationHistory
                .Where(record => record.CompletionVerdictRule == "deferred-no-change-round")
                .Select(record => DeferredNoChangeOutcome.TryParse(record.StandardError, out var outcome) ? outcome : null)
                .OfType<DeferredNoChangeOutcome>()
                .DistinctBy(outcome => outcome.CandidateSha + ":" + string.Join(',', outcome.TestClasses));
            foreach (var outcome in outcomes)
            {
                var locators = goal.Timeline.Select((evt, index) => (Event: evt, Index: index))
                    .Where(item => item.Event.Kind is ProgressKind.FindingEvidenceRequestRecorded or ProgressKind.FindingEvidenceRunRecorded)
                    .Select(item => (Entry: DeferredNoChangeEvidenceIndexLines.TryParse(item.Event.Message, out var entry) ? entry : null, item.Index))
                    .Where(item => item.Entry is not null && item.Entry.DeveloperTaskId == developer.Id &&
                        string.Equals(item.Entry.CandidateSha, outcome.CandidateSha, StringComparison.OrdinalIgnoreCase) &&
                        DeferredNoChangeEvidenceIndexLines.MatchesDeclaration(item.Entry, outcome.TestClasses))
                    .ToArray();
                var lastCompletedLocatorIndex = locators.Where(item => !string.IsNullOrWhiteSpace(item.Entry!.ResultPath))
                    .GroupBy(item => item.Entry!.ResultPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Min(item => item.Index)).DefaultIfEmpty(-1).Max();
                var applicable = locators.Where(item => !string.IsNullOrWhiteSpace(item.Entry!.ResultPath) || item.Index > lastCompletedLocatorIndex)
                    .GroupBy(item => item.Entry!.ResultPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => (Entry: group.Last().Entry, Index: string.IsNullOrWhiteSpace(group.Key)
                        ? group.Max(item => item.Index) : group.Min(item => item.Index))).ToArray();
                if (applicable.Length == 0)
                    captured.Add((CaptureDeferredReceipt(goal, developer.Id, outcome, null, validatedForTests), int.MaxValue));
                foreach (var item in applicable)
                    captured.Add((CaptureDeferredReceipt(goal, developer.Id, outcome, item.Entry, validatedForTests), item.Index));
            }
        }

        // A new requesting Developer is not a new evidence identity. An authenticated attempt
        // for the same candidate/declaration remains usable regardless of which task asks for it.
        captured = captured.Where(item => item.Receipt.Diagnostic != "deferred-evidence-locator-unavailable" ||
            !captured.Any(other => other.Receipt.AttemptId is not null &&
                string.Equals(other.Receipt.CandidateSha, item.Receipt.CandidateSha, StringComparison.OrdinalIgnoreCase) &&
                other.Receipt.Declared.ToHashSet(StringComparer.Ordinal).SetEquals(item.Receipt.Declared))).ToList();

        // A malformed locator must not cause a fallback to an older passing marker.
        var malformed = goal.Timeline.Any(evt =>
            evt.Message.StartsWith("finding-evidence deferred-no-change ", StringComparison.Ordinal) &&
            !DeferredNoChangeEvidenceIndexLines.TryParse(evt.Message, out _));
        if (malformed)
            captured.Add((new("unknown", currentCandidateSha ?? "unknown", null, null, null, false,
                DeferredNoChangeEvidenceAvailability.Unavailable, [], [], [], null, null,
                "deferred-evidence-locator-malformed"), int.MaxValue));

        var candidates = captured.Where(item => string.Equals(item.Receipt.CandidateSha, currentCandidateSha,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        var newestAttempt = candidates.Where(item => item.Receipt.AttemptId is not null)
            .OrderByDescending(item => item.Receipt.Ordinal)
            .ThenByDescending(item => item.Receipt.StartedAt)
            .ThenByDescending(item => item.TimelineIndex)
            .FirstOrDefault();
        // Attempt metadata orders completed runs. A no-attempt barrier overrides that run only
        // when recorded later; repeating an old result locator cannot move its first position.
        var current = candidates.Where(item => item.Receipt.AttemptId is null &&
                (newestAttempt.Receipt is null || item.TimelineIndex > newestAttempt.TimelineIndex))
            .OrderByDescending(item => item.TimelineIndex).FirstOrDefault();
        if (current.Receipt is null) current = newestAttempt;
        return new(goal.Id.Value, currentCandidateSha, captured.Select(item => item.Receipt with
        {
            IsCurrent = !string.IsNullOrWhiteSpace(currentCandidateSha) && ReferenceEquals(item.Receipt, current.Receipt)
        }).ToArray());
    }

    private DeferredNoChangeEvidenceSnapshot CaptureDeferredReceipt(
        Goal goal, TaskId developerId, DeferredNoChangeOutcome outcome, DeferredNoChangeEvidenceEntry? locator, Action? validatedForTests)
    {
        var snapshot = new DeferredNoChangeEvidenceSnapshot(developerId.Value, outcome.CandidateSha,
            null, null, null, false, DeferredNoChangeEvidenceAvailability.Unavailable,
            [], outcome.TestClasses.ToArray(), [], null, null, "deferred-evidence-locator-unavailable");
        if (locator is null) return snapshot;
        snapshot = snapshot with { NotRun = locator.NotRun.ToArray() };
        if (string.IsNullOrWhiteSpace(locator.ResultPath))
            return snapshot with
            {
                Availability = locator.Outcome == "started" ? DeferredNoChangeEvidenceAvailability.Pending : DeferredNoChangeEvidenceAvailability.Unavailable,
                Diagnostic = locator.Outcome == "started" ? "deferred-evidence-pending" : "deferred-evidence-unavailable-not-run"
            };

        var handles = new List<FileStream>();
        var sourceHashes = new List<string>();
        try
        {
            var directory = Path.GetFullPath(Path.Combine(_rootDirectory, goal.Id.Value));
            if (!Path.IsPathFullyQualified(locator.ResultPath))
                throw new InvalidDataException("deferred-evidence-result-custody");
            var path = Path.GetFullPath(locator.ResultPath);
            RequireDeferredCustody(path, directory, allowMissingLeaf: true);
            if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".result.json", StringComparison.Ordinal))
                throw new InvalidDataException("deferred-evidence-result-custody");
            var metadataPath = path[..^".result.json".Length] + ".attempt.json";
            RequireDeferredCustody(metadataPath, directory);
            var metadataBytes = CaptureBytes(metadataPath);
            var attempt = ReadCanonicalAttempt(metadataPath, goal.Id.Value);
            snapshot = snapshot with { AttemptId = attempt.AttemptId, Ordinal = attempt.Ordinal, StartedAt = attempt.StartedAt };
            var canonicalResult = Path.Combine(directory, attempt.AttemptId + ".result.json");
            if (!string.Equals(path, canonicalResult, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(attempt.ResultPath), canonicalResult, StringComparison.OrdinalIgnoreCase) ||
                attempt.Kind != PreReviewEvidenceDispatchKind || attempt.FindingRoundFingerprint != "deferred-no-change" ||
                !string.Equals(attempt.BranchHeadSha, outcome.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(attempt.FocusedEvidenceBatchId) || attempt.SupersededBy is not null ||
                attempt.SupersessionCause is not null || attempt.FocusedEvidenceRunsBaselineArm ||
                attempt.FocusedEvidenceNegativeControl is not null || attempt.FocusedEvidenceMutation is not null ||
                attempt.FocusedEvidenceRevertPaths is not null)
                throw new InvalidDataException("deferred-evidence-attempt-identity");

            var selections = ParseDeferredSelections(attempt.FocusedEvidenceRequest);
            var names = selections.Select(selection => $"{selection.TestProject}:{selection.TestClass}").ToArray();
            if (selections.Length == 0 || locator.NotRun.Count != 0 ||
                !names.SequenceEqual(locator.Selections, StringComparer.Ordinal) ||
                !selections.Select(selection => selection.TestClass).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(outcome.TestClasses) ||
                !(attempt.FocusedEvidenceMemberRequests ?? []).SequenceEqual(names, StringComparer.Ordinal))
                throw new InvalidDataException("deferred-evidence-selection-mismatch");
            var syntheticId = ConductorDriver.CreateFindingEvidenceReceiptId(outcome.CandidateSha,
                "deferred-no-change", FindingEvidenceExecutionClassifier.BuildRequestIdentity(new FindingEvidenceRequest(selections)));
            if (locator.ReceiptId != syntheticId ||
                attempt.FocusedEvidenceReceiptId is not null && attempt.FocusedEvidenceReceiptId != syntheticId)
                throw new InvalidDataException("deferred-evidence-receipt-identity");
            snapshot = snapshot with { Selections = names };
            if (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running)
                return snapshot with { Availability = DeferredNoChangeEvidenceAvailability.Pending, Diagnostic = "deferred-evidence-pending" };
            if (attempt.CompletedAt is null || attempt.Outcome is not
                (ConductorParallelAcceptanceAttemptOutcome.Passed or ConductorParallelAcceptanceAttemptOutcome.Failed))
                throw new InvalidDataException("deferred-evidence-terminal-unusable");

            var resultBytes = CaptureBytes(canonicalResult);
            var result = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(resultBytes, JsonOptions);
            // Historical focused results use the run-artifact's default DispatchKind (gate).
            // The canonical attempt's Kind plus the result's focused-evidence shape own this boundary.
            if (result is not { Kind: "focused-evidence", FocusedEvidence: { } evidence } ||
                !string.Equals(result.BranchHeadSha, attempt.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.MainHeadSha, attempt.MainHeadSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(evidence.Request, attempt.FocusedEvidenceRequest, StringComparison.Ordinal) ||
                evidence.NegativeControlOutcome is not null ||
                evidence.Arms is not { Count: 1 } || evidence.Arms[0] is not { Arm: FindingEvidenceArm.Candidate, Checks: not null } ||
                evidence.Arms[0].Checks.Any(check => check is null) ||
                !string.Equals(evidence.Arms[0].Sha, attempt.BranchHeadSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("deferred-evidence-result-identity");

            var arm = evidence.Arms[0];
            var paths = arm.Checks.SelectMany(check => check.TestResultPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            // The owning store or this goal's build-artifact namespace owns evidence paths.
            var buildArtifacts = DotnetBuildStorageLayout.GoalArtifactsPath(goal.Id, _buildStorageRoot);
            var trx = paths.Select(trxPath =>
            {
                if (new DotnetBuildStorageRoot(directory).ContainsPath(trxPath)) RequireDeferredCustody(trxPath, directory);
                else
                {
                    if (!new DotnetBuildStorageRoot(DotnetBuildStorageLayout.GoalRoot(goal.Id, _buildStorageRoot)).ContainsPath(buildArtifacts))
                        throw new InvalidDataException("deferred-evidence-build-custody");
                    RequireDeferredCustody(trxPath, buildArtifacts);
                }
                var bytes = CaptureBytes(trxPath);
                return new { Path = trxPath, Sha256 = Hash(bytes), ByteLength = bytes.Length, ContentBase64 = Convert.ToBase64String(bytes) };
            }).ToArray();
            var receipt = ConductorDriver.CreateFindingEvidenceArmReceipt(arm);
            var green = attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Passed &&
                evidence.IsValidEvidence && receipt is { Accepted: true, Passed: true,
                    Disposition: FindingEvidenceArmDisposition.Green, ExecutedTestCount: > 0 } &&
                (arm.NotRunSelections?.Count ?? 0) == 0 &&
                AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(receipt.TestResultPaths,
                    receipt.ReceiptArtifacts, receipt.ExecutedTestCount.Value,
                    selections.Select(selection => selection.TestClass).ToArray(), allowShortNamePrefixCoverage: true);
            var red = attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Failed && evidence.Accepted &&
                !evidence.Passed && receipt is { Accepted: true, Passed: false, Disposition: FindingEvidenceArmDisposition.Red };
            var availability = green ? DeferredNoChangeEvidenceAvailability.Green : red
                ? DeferredNoChangeEvidenceAvailability.Red : DeferredNoChangeEvidenceAvailability.Unavailable;
            validatedForTests?.Invoke();
            for (var index = 0; index < handles.Count; index++)
            {
                handles[index].Position = 0;
                if (Convert.ToHexStringLower(SHA256.HashData(handles[index])) != sourceHashes[index])
                    throw new InvalidDataException("deferred-evidence-content-changed");
            }
            var body = JsonSerializer.Serialize(new
            {
                ContractVersion = 1, GoalId = goal.Id.Value, DeveloperTaskId = developerId.Value, attempt.AttemptId,
                attempt.Ordinal, attempt.StartedAt, attempt.CompletedAt, attempt.BranchHeadSha,
                SourceReceiptId = attempt.FocusedEvidenceReceiptId, SyntheticReceiptId = syntheticId,
                attempt.FocusedEvidenceBatchId, attempt.FindingRoundFingerprint,
                Selections = names, Declared = outcome.TestClasses, locator.NotRun,
                TerminalOutcome = attempt.Outcome.ToString(), Availability = availability.ToString(),
                MetadataSha256 = Hash(metadataBytes), ResultSha256 = Hash(resultBytes),
                Result = JsonSerializer.Deserialize<JsonElement>(resultBytes), CandidateArm = receipt, Trx = trx,
                NegativeControlExecuted = false
            });
            return snapshot with { Availability = availability, CandidateArm = receipt, BodyJson = body,
                Diagnostic = green || red ? null : "deferred-evidence-validation-unavailable" };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or OverflowException)
        {
            return snapshot with { Availability = DeferredNoChangeEvidenceAvailability.Unavailable,
                CandidateArm = null, BodyJson = null, Diagnostic = ex is InvalidDataException
                    ? ex.Message : "deferred-evidence-unreadable" };
        }
        finally { foreach (var handle in handles) handle.Dispose(); }

        byte[] CaptureBytes(string file)
        {
            var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            handles.Add(stream); // Keep every validated source stable until the immutable body is captured.
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            sourceHashes.Add(Hash(bytes));
            return bytes;
        }
        static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static FindingEvidenceSelection[] ParseDeferredSelections(string? request)
    {
        var selections = new List<FindingEvidenceSelection>();
        foreach (var token in (request ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = token.Split(':', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || !DeveloperDeferredTestClassNames.IsValid(pair[1])) return [];
            selections.Add(new(pair[0], pair[1]));
        }
        return selections.ToArray();
    }

    private static void RequireDeferredCustody(string path, string directory, bool allowMissingLeaf = false)
    {
        if (!new DotnetBuildStorageRoot(directory).ContainsPath(path))
            throw new InvalidDataException("deferred-evidence-path-outside-custody");
        // Lexical membership alone cannot authorize a filesystem link into another store.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (allowMissingLeaf && string.Equals(current, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && !File.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("deferred-evidence-linked-path");
        }
    }
}
