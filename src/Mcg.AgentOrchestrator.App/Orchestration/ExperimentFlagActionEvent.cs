using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentFlagActionEvent
{
    internal const string Kind = "experiment-flag-action";

    internal static string Detail(ExperimentRecord record, string action, bool? before)
    {
        if (action is not ("revert" or "keep" or "toggle"))
            throw new ArgumentException("Expected revert, keep or toggle.", nameof(action));
        if (record.Spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag ||
            record.Spec.Intervention.FlagTarget is not { } target)
            throw new ArgumentException("Expected a ConfigFlag experiment with a flag target.", nameof(record));

        var file = target.FileKind switch
        {
            ExperimentFlagFileKind.ConductorPolicy => "conductor-policy",
            ExperimentFlagFileKind.RemoteLaneExecutors => "remote-lane-executors",
            _ => throw new ArgumentException("Unknown experiment flag file kind.", nameof(record))
        };
        bool? after = action switch
        {
            "revert" => target.PriorValue,
            "keep" => target.ValueToApply,
            _ => before is null ? null : before == target.ValueToApply ? target.PriorValue : target.ValueToApply
        };
        return $"EXPERIMENT_FLAG_ACTION experiment={record.Id} action={action} file={file} " +
            $"property={target.PropertyName} from={Value(before)} to={Value(after)}" +
            (action == "keep" ? $" backlog={ConductorExperimentFlagKeepController.BacklogId(record.Id)}" : "");
    }

    private static string Value(bool? value) => value is null ? "unknown" : value.Value ? "true" : "false";
}
