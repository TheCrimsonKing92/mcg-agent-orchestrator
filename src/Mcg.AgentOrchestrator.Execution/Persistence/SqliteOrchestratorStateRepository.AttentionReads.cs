using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    public async Task<AttentionReadProjection?> ReadAttentionProjectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = OpenConnection();
        await using var archiveTable = connection.CreateCommand();
        archiveTable.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'goal_snapshot_archive'";
        var hasArchive = (long)(await archiveTable.ExecuteScalarAsync(cancellationToken))! != 0;
        var facts = new List<AttentionGoalReadFacts>();
        var gates = new List<AttentionGateEvidence>();
        var questions = new List<AttentionOwnerQuestionEvidence>();
        var providers = new List<ProviderUsageGoalEvidence>();
        var archiveReads = 0;
        var archiveDecodes = 0;

        await using var command = connection.CreateCommand();
        // LoadGoalsAsync's primary-key IN scan supplies the baseline goal order. Archives only
        // replace metadata stubs, exactly as GoalSnapshotArchive.HydrateAsync does.
        command.CommandText = "SELECT id, snapshot_json FROM goals ORDER BY id";
        _statementObserver?.Invoke(command.CommandText);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hot = JsonDocument.Parse(reader.GetString(1));
            var root = hot.RootElement;
            JsonDocument? archive = null;
            try
            {
                if (ReadAttentionField<bool>(root, "IsMetadataOnly") && hasArchive)
                {
                    await using var readArchive = connection.CreateCommand();
                    readArchive.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id";
                    readArchive.Parameters.AddWithValue("$id", reader.GetString(0));
                    _statementObserver?.Invoke(readArchive.CommandText);
                    var value = await readArchive.ExecuteScalarAsync(cancellationToken);
                    if (value is not null and not DBNull)
                    {
                        archiveReads++;
                        var json = GoalSnapshotArchiveCodec.Decode(value)!;
                        archiveDecodes++;
                        archive = JsonDocument.Parse(json);
                        root = archive.RootElement;
                    }
                }

                var id = ReadAttentionField<string>(root, "Id") ?? reader.GetString(0);
                var status = ReadAttentionField<GoalStatus>(root, "Status");
                var metadataOnly = ReadAttentionField<bool>(root, "IsMetadataOnly");
                var versions = metadataOnly ? [] : ReadAttentionArray<GoalBriefVersion>(root, "BriefVersions");
                var briefVersion = versions.Length == 0 ? 1 : versions.Single(version => version.IsAuthoritative).Version;
                facts.Add(new(id, status, briefVersion, !metadataOnly && AttentionElements(root, "Tasks")
                    .Any(task => ReadAttentionField<WorkTaskStatus>(task, "Status") == WorkTaskStatus.WaitingForHuman)));
                if (metadataOnly) continue; // A missing archive retains the baseline metadata-only behavior.

                var obligations = new List<GateTestObligation>();
                foreach (var obligation in ReadAttentionArray<GateTestObligation>(root, "GateTestObligations"))
                {
                    var previous = obligations.FirstOrDefault(item => item.HasSameIdentity(obligation));
                    if (previous is not null)
                    {
                        if (obligation.PlannerIdentity is not null &&
                            !obligation.PlannerEvidence!.SemanticallyEquals(previous.PlannerEvidence))
                            throw new InvalidOperationException("Conflicting stored Planner evidence payload.");
                        continue;
                    }
                    obligations.Add(obligation with { Selectors = obligation.Selectors?.ToArray() ?? [] });
                }
                gates.AddRange(obligations.Where(item => item.BriefVersion == briefVersion)
                    .Select(item => new AttentionGateEvidence(id, item)));

                var hold = ReadAttentionField<GoalHoldSnapshot>(root, "CurrentHold");
                var needsQuestion = status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded) &&
                    hold?.State is "steward-owner-question" or "author-owner-question";
                var taskIds = new List<string>();
                var tasks = new List<ProviderUsageTaskEvidence>();
                foreach (var task in AttentionElements(root, "Tasks"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var taskId = ReadAttentionField<string>(task, "Id")!;
                    if (needsQuestion) taskIds.Add(taskId);
                    var dispatches = AttentionElements(task, "DispatchHistory").Select(ReadAttentionDispatch).ToArray();
                    var lastDispatch = task.TryGetProperty("LastDispatch", out var last) && last.ValueKind != JsonValueKind.Null
                        ? ReadAttentionDispatch(last) : null;
                    // Undispatched tasks matter only as the target of a reserved probe. They carry
                    // no verification graph, execution output, retry state, process or worker brief.
                    var taskStatus = ReadAttentionField<WorkTaskStatus>(task, "Status");
                    var agentId = ReadAttentionField<string>(task, "AssignedAgentId");
                    if (dispatches.Length == 0 && lastDispatch is null &&
                        (taskStatus != WorkTaskStatus.Assigned || agentId is null)) continue;
                    tasks.Add(new(taskId, ReadAttentionField<AgentRole>(task, "RequiredRole"), taskStatus, agentId, lastDispatch, dispatches,
                        ReadAttentionVerificationField(task, "LastVerification"),
                        AttentionElements(task, "VerificationHistory").Select(ReadAttentionVerification).ToArray(),
                        ReadAttentionField<DateTimeOffset?>(task, "LatestProviderBudgetRecoveryAt")));
                }
                if (needsQuestion) questions.Add(new(id, hold!, taskIds));
                var timeline = AttentionElements(root, "Timeline").Select(evt => new ProgressEvent(
                        new GoalId(id), ReadAttentionField<string>(evt, "TaskId") is { } taskId ? new TaskId(taskId) : null,
                        ReadAttentionField<ProgressKind>(evt, "Kind"), ReadAttentionField<string>(evt, "Message")!,
                        ReadAttentionField<DateTimeOffset>(evt, "OccurredAt")))
                    .Where(evt => evt.Kind is ProgressKind.TaskRedelegated or ProgressKind.ProviderHoldSubstituted or
                        ProgressKind.ProviderCapabilitySubstituted or ProgressKind.TaskCancelled ||
                        ProviderBudgetHoldSchedules.TryParseProbe(evt, out _) || ProviderBudgetHoldSchedules.TryParseReset(evt, out _))
                    .ToArray();
                if (tasks.Count > 0 || timeline.Length > 0) providers.Add(new(id, status, tasks, timeline));
            }
            finally { archive?.Dispose(); }
        }
        return new(facts, gates, questions, providers, new(facts.Count, archiveReads, archiveDecodes,
            providers.Sum(goal => goal.Tasks.Count), questions.Sum(question => question.TaskIds.Count)));
    }

    private static T? ReadAttentionField<T>(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<T>(SerializerOptions) : default;

    private static IEnumerable<JsonElement> AttentionElements(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.EnumerateArray() : [];

    private static T[] ReadAttentionArray<T>(JsonElement element, string name) =>
        AttentionElements(element, name).Select(value => value.Deserialize<T>(SerializerOptions)!).ToArray();

    private static TaskDispatchSnapshot ReadAttentionDispatch(JsonElement value) => new(
        ReadAttentionField<string>(value, "WorkerName")!, ReadAttentionField<string>(value, "Command")!,
        ReadAttentionField<string>(value, "WorkingDirectory")!, ReadAttentionField<DateTimeOffset>(value, "DispatchedAt"),
        ProviderName: ReadAttentionField<string>(value, "ProviderName"),
        ClaudeCredentialSourceDirectory: ReadAttentionField<string>(value, "ClaudeCredentialSourceDirectory"));

    private static TaskVerificationSnapshot? ReadAttentionVerificationField(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? ReadAttentionVerification(value) : null;

    // Structured verification fields participate in TaskSpec's history retention and same-round
    // merge rules. Keep that canonical bounded record intact rather than reimplementing the rules.
    private static TaskVerificationSnapshot ReadAttentionVerification(JsonElement value) =>
        value.Deserialize<TaskVerificationSnapshot>(SerializerOptions)!;
}
