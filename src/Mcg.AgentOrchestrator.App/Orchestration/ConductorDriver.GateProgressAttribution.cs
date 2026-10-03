using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static string CohortGateRunIdentity(IEnumerable<string> memberGoalIds) =>
        string.Join(':', memberGoalIds.Order(StringComparer.Ordinal));

    internal static string TrainGateRunIdentity(IEnumerable<string> memberGoalIds) =>
        $"train:{string.Join('+', memberGoalIds.Order(StringComparer.Ordinal))}";

    private static AcceptanceRunExecutionOptions CreateCohortAttributionExecutionOptions(
        ConductEventLogWriter writer,
        AcceptanceCohortIdentity identity,
        AcceptanceCohortMemberBinding member) =>
        new(ProgressSink: progress =>
        {
            var goalId = member.GoalId.Value[..8];
            var detail = FormatGateProgressConductEvent(progress with { GoalId = member.GoalId.Value }) +
                $" cohort={identity.Value[..Math.Min(18, identity.Value.Length)]} member={goalId} " +
                $"members={string.Join(',', identity.Members.Select(binding => binding.GoalId.Value[..8]))} scope=attribution";
            TryAppendGateProgressEvent(writer, goalId, detail);
        }, GateRunIdentity: CohortGateRunIdentity(identity.Members.Select(binding => binding.GoalId.Value)));

    private static void AppendCohortAttributionStartEvent(
        ConductEventLogWriter writer,
        AcceptanceCohortIdentity identity,
        IReadOnlyList<AcceptanceCohortMemberBinding> bindings)
    {
        var members = string.Join(',', bindings.Select(member => member.GoalId.Value[..8]));
        TryAppendGateProgressEvent(
            writer,
            goalId: null,
            $"ATTRIBUTION_START cohort={identity.Value[..Math.Min(18, identity.Value.Length)]} " +
            $"members={members} scope=attribution",
            eventKind: "cohort-attribution");
    }

    private AcceptanceRunExecutionOptions CreateMergeTrainGateExecutionOptions(
        MergeTrainIdentity identity,
        IReadOnlyList<MergeTrainMemberBinding> members,
        string trainKey)
    {
        var workspace = _cohortWorkspace ?? throw new InvalidOperationException("Production acceptance cohort workspace is unavailable.");
        var logPath = Path.Combine(
            workspace.ExecutionDirectory, ".orchestrator", "logs", ConductEventLogWriter.CurrentFileName);
        ConductEventLogWriter? writer = null;
        var memberIds = string.Join(',', members.Select(member => member.GoalId.Value[..8]));
        var trainId = identity.Value[..Math.Min(MergeTrainIdentity.Version.Length + 9, identity.Value.Length)];
        return new AcceptanceRunExecutionOptions(ProgressSink: progress =>
        {
            try
            {
                writer ??= new ConductEventLogWriter(logPath);
                foreach (var member in members)
                {
                    var goalId = member.GoalId.Value[..8];
                    var detail = FormatGateProgressConductEvent(progress with { GoalId = member.GoalId.Value }) +
                        $" train={trainId} member={goalId} members={memberIds} scope=merge-train";
                    TryAppendGateProgressEvent(writer, goalId, detail);
                }
            }
            catch (Exception)
            {
                // Event-log setup is best-effort for the gate attempt.
            }
        }, GateRunIdentity: trainKey);
    }

    private static void TryAppendGateProgressEvent(
        ConductEventLogWriter writer,
        string? goalId,
        string detail,
        string eventKind = "gate-progress")
    {
        try
        {
            _ = writer.AppendRequired(eventKind, goalId, detail);
        }
        catch (Exception)
        {
            // Progress is observational; a log failure must not change a gate outcome.
        }
    }
}
