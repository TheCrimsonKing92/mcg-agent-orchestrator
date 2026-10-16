using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorOwnerQuestionHold(
    string TargetKind, string TargetId, string Reason, string Question,
    string Recommendation, OwnerQuestionReversibility Reversibility)
{
    internal string BlastRadius => $"{Reversibility switch
    {
        OwnerQuestionReversibility.Reversible => "reversible",
        OwnerQuestionReversibility.CriterionWeakening => "criterion-weakening",
        _ => "irreversible"
    }} (Author reason: {Reason})";

    internal static AuthorOwnerQuestionHold? TryParse(GoalHoldState? hold)
    {
        const string prefix = "author-owner-question item=";
        const string reasonMarker = " reason=";
        const string questionMarker = " question=";
        const string recommendationMarker = " recommendation=";
        if (hold?.State != "author-owner-question" ||
            hold.Blocker is not { } blocker || !blocker.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var itemEnd = blocker.IndexOf(' ', prefix.Length);
        if (itemEnd < 0) return null;
        var item = blocker[prefix.Length..itemEnd];
        var colon = item.IndexOf(':');
        if (colon <= 0 || colon == item.Length - 1) return null;
        var reasonStart = blocker.IndexOf(reasonMarker, itemEnd, StringComparison.Ordinal);
        if (reasonStart != itemEnd) return null;
        reasonStart += reasonMarker.Length;
        var questionStart = blocker.IndexOf(questionMarker, reasonStart, StringComparison.Ordinal);
        if (questionStart <= reasonStart) return null;
        var reason = blocker[reasonStart..questionStart];
        questionStart += questionMarker.Length;
        var recommendationStart = blocker.LastIndexOf(recommendationMarker, StringComparison.Ordinal);
        if (recommendationStart <= questionStart) return null;
        var question = blocker[questionStart..recommendationStart];
        recommendationStart += recommendationMarker.Length;
        if (recommendationStart == blocker.Length) return null;

        return new(item[..colon], item[(colon + 1)..], reason, question,
            blocker[recommendationStart..], Classify(reason));
    }

    internal static OwnerQuestionReversibility Classify(string reason) => reason switch
    {
        "model-ask-owner" or "missing-evidence" => OwnerQuestionReversibility.Reversible,
        "acceptance-weakening" => OwnerQuestionReversibility.CriterionWeakening,
        _ when reason.StartsWith("frozen-fact-ruling", StringComparison.Ordinal) =>
            OwnerQuestionReversibility.CriterionWeakening,
        _ => OwnerQuestionReversibility.Irreversible
    };
}
