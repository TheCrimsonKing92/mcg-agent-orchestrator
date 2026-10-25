using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class StoreSetupCommandKinds
{
    internal static bool RequiresBacklogState(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "backlog-list" or "backlog-show" or "backlog-depends" or "backlog-similar" => true,
            "backlog-add" => HasFlag(args, "--depends-on") || !HasFlag(args, "--no-similar"),
            _ => false
        };
    }

    internal static bool IsInboxBackedTaskMutation(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].ToLowerInvariant() is
            OperatorIntentVerbs.Progress or
            OperatorIntentVerbs.Retry or
            OperatorIntentVerbs.VerifyManual or
            OperatorIntentVerbs.Adjudicate;

    internal static bool IsCriterionEvidenceMutation(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].ToLowerInvariant() is
            OperatorIntentVerbs.CriterionEvidenceMap or
            OperatorIntentVerbs.CriterionEvidenceRecord or
            OperatorIntentVerbs.CriterionEvidenceRepair;

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));
}
