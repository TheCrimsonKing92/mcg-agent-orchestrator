using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private void TryRecordSoloMainSuspect(Goal goal, string goalPrefix,
        AcceptanceVerificationSummary acceptance, IReadOnlyList<ExcludedAcceptanceFailure> excluded,
        string? candidateSha, string? mainSha, IReadOnlyList<string>? changedPaths)
    {
        if (_cohortWorkspace is not { } workspace || _cohortAcceptanceStore is not { } store ||
            excluded.Count == 0 || excluded.Any(failure => failure.Kind != AcceptanceRetryExclusionKind.Inherited) ||
            string.IsNullOrWhiteSpace(candidateSha) || string.IsNullOrWhiteSpace(mainSha) || changedPaths is null)
            return;

        ConductEventLogWriter? writer = null;
        try
        {
            writer = new ConductEventLogWriter(workspace.ConductEventsLogPath);
            // The focused baseline SHA is already carried by each inherited attribution's evidence.
            // Missing or differently shaped evidence cannot corroborate observed main.
            const string prefix = "same focused identity failed at merge-base ";
            var identities = excluded.Select(failure => failure.Identity)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var attributions = acceptance.RequiredUnmetCriteria
                .SelectMany(check => check.FailingTestAttributions ?? []).ToArray();
            foreach (var identity in identities)
            {
                var evidence = attributions.Where(item => item.TestIdentity == identity).ToArray();
                if (evidence.Length == 0 || evidence.Any(item =>
                    item.Origin != AcceptanceTestFailureOrigin.Inherited ||
                    !item.Evidence.StartsWith(prefix, StringComparison.Ordinal) ||
                    !string.Equals(item.Evidence[prefix.Length..], mainSha, StringComparison.OrdinalIgnoreCase)))
                    return;
            }

            store.RecordSoloInheritedReceipt(new(goal.Id, candidateSha, mainSha, mainSha, identities, changedPaths));
            foreach (var earlier in store.ReadSoloInheritedReceipts(mainSha, goal.Id, candidateSha))
            {
                var shared = ConductorAcceptanceCohortMainSuspect.KeepUntouched(
                    identities.Intersect(earlier.InheritedTests, StringComparer.Ordinal),
                    changedPaths.Concat(earlier.ChangedPaths), workspace.ExecutionDirectory,
                    test => AcceptanceTestSourceResolver.ResolveSourcePaths(workspace.ExecutionDirectory, null, test));
                if (shared.Count == 0) continue;

                var fingerprint = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(string.Join('\n', shared)))).ToLowerInvariant();
                var tests = ConductorAcceptanceCohortMainSuspect.FormatTests(shared);
                var goals = $"{earlier.GoalId.Value[..8]},{goalPrefix}";
                var detail = $"main-suspect source=solo goals={goals} shared-failing-tests={shared.Count} tests={tests}";
                var now = _utcNow();
                var events = new PostLandingCanaryEventStore(
                    new SqliteRunEventStore(workspace.RunEventStorePath), workspace.RunEventStorePath);
                var appended = events.AppendOnceAsync(PostLandingCanaryEventKind.Failed,
                    new PostLandingCanaryEventPayload(PostLandingCanaryEventPayload.CanaryTag,
                        mainSha, [], ConductorAcceptanceCohortMainSuspect.FailureToken, shared.Count, detail, null, now,
                        SharedFailingTests: shared),
                    ConductorAcceptanceCohortMainSuspect.SoloEventId(mainSha, fingerprint), now)
                    .GetAwaiter().GetResult().Appended;
                if (appended)
                    TryAppendGateProgressEvent(writer, goalId: null,
                        $"CANARY_GATE sha={mainSha} result=failed reason=main-suspect source=solo goals={goals} tests={tests}",
                        eventKind: "canary-gate");
                break;
            }
        }
        catch (Exception ex)
        {
            // Corroboration is additive: storage faults leave the existing goal-local hold intact.
            if (writer is not null)
                TryAppendGateProgressEvent(writer, goal.Id.Value,
                    $"solo-main-suspect result=error reason={ex.GetType().Name}", eventKind: "solo-main-suspect");
        }
    }
}
