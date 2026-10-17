using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Grouped children publish train evidence, while the parent tick owns goal checkpoints.
internal static class MergeTrainAttributedFailureReconciliation
{
    internal static IReadOnlySet<GoalId> Reconcile(
        OrchestratorWorkspace? workspace, AgentOrchestratorKernel? kernel, MergeTrainAcceptanceStore? store,
        Func<Goal, GateReadyCandidateProjectionResult> project,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        ISet<GoalId>? checkpointGoalIds = null)
    {
        var changed = new HashSet<GoalId>();
        if (workspace is null || kernel is null || store is null) return changed;
        Dictionary<string, List<(string TrainId, string Revision)>> evidenceByGoal;
        try { evidenceByGoal = ReadEvidence(workspace); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            Console.Error.WriteLine($"TRAIN_FAILURE_RECONCILIATION_ERROR error={ex.GetType().Name}: {ex.Message}");
            return changed;
        }
        foreach (var goal in kernel.Goals.Where(goal => goal.Status == GoalStatus.Verified).ToArray())
        {
            var beforeFailure = goal.LatestAcceptanceFailure;
            var applying = false;
            try
            {
                if (!evidenceByGoal.TryGetValue(goal.Id.Value, out var evidence) ||
                    project(goal) is not GateReadyCandidateProjectionResult.Ready ready) continue;
                foreach (var (trainId, revision) in evidence)
                {
                    if (revision != ready.Projection.CandidateRevision) continue;
                    var receipt = store.TryReadReceipt(trainId);
                    if (receipt is null || WasApplied(goal, receipt.ReceiptId) ||
                        !MergeTrainRedAttribution.IsGenuineTrainRed(receipt) ||
                        MergeTrainReachAttribution.ReadFailingClasses(receipt.GateTestResultPaths) is null) continue;
                    var member = receipt.Identity.Members.SingleOrDefault(member =>
                        member.GoalId == goal.Id && member.CandidateRevision == revision);
                    if (member is null) continue;
                    // Routing writes the receipt identity into the goal's durable timeline.
                    // That event survives failure clearing by an operator re-gate or restore.
                    applying = true;
                    MergeTrainAttributedFailure.Apply(receipt, member, goal, "recorded", 3, [],
                        _ => throw new InvalidOperationException("Parent reconciliation cannot record another ejection."),
                        recordFailure, kernel.RouteRecordedAcceptanceFailure);
                    break;
                }
            }
            catch (Exception ex) when (!applying && ex is IOException or UnauthorizedAccessException or
                JsonException or SqliteException or InvalidOperationException or ArgumentException)
            {
                var message = $"TRAIN_FAILURE_RECONCILIATION_ERROR goal={goal.Id.Value} error={ex.GetType().Name}: {ex.Message}";
                if (goal.Timeline.LastOrDefault()?.Message != message)
                {
                    kernel.RecordGoalPolicyDecision(goal.Id, message);
                    checkpointGoalIds?.Add(goal.Id);
                    Console.Error.WriteLine(message);
                }
            }
            finally
            {
                // Keep earlier mutations checkpointable even if a later member fails loudly.
                if (goal.Status != GoalStatus.Verified || !ReferenceEquals(beforeFailure, goal.LatestAcceptanceFailure))
                {
                    changed.Add(goal.Id);
                    checkpointGoalIds?.Add(goal.Id);
                }
            }
        }
        return changed;
    }

    private static Dictionary<string, List<(string TrainId, string Revision)>> ReadEvidence(OrchestratorWorkspace workspace)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"),
            Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // Ejections have no typed read API or receipt foreign key. Bind the existing
        // receipt detail to the implicated identity rather than inferring from exclusion alone.
        command.CommandText = """
            SELECT DISTINCT i.goal_id, i.train_id, i.candidate_revision
            FROM merge_train_implicated_candidates i
            JOIN merge_train_receipts r ON r.train_id = i.train_id
            JOIN merge_train_ejections e ON e.goal_id = i.goal_id
                AND e.detail = 'Dropped after RED receipt ' || r.receipt_id || '.'
            WHERE e.reason = 'RedAttributedMember';
            """;
        var evidenceByGoal = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var goalId = reader.GetString(0);
            if (!evidenceByGoal.TryGetValue(goalId, out var evidence)) evidenceByGoal.Add(goalId, evidence = []);
            evidence.Add((reader.GetString(1), reader.GetString(2)));
        }
        return evidenceByGoal;
    }

    internal static IReadOnlyList<Goal> PrepareAdmission(ConductorDriver driver, IReadOnlyList<Goal> eligible,
        ConductorAutonomyPolicy policy, ISet<GoalId> changedGoalIds, List<string>? changedGoalLines = null)
    {
        var changed = driver.ReconcileAttributedTrainFailures(policy, changedGoalIds);
        foreach (var goalId in changed)
            changedGoalLines?.Add($"TRAIN_FAILURE_RECONCILED goal={goalId.Value} status=AcceptanceFailed");
        return eligible.Where(goal => !changed.Contains(goal.Id)).ToArray();
    }

    private static bool WasApplied(Goal goal, string receiptId) => goal.Timeline.Any(record =>
        record.Kind == ProgressKind.GoalPolicyDecision &&
        record.Message.StartsWith("merge train RED attributed (", StringComparison.Ordinal) &&
        record.Message.Contains($") receipt {receiptId}:", StringComparison.Ordinal));
}
