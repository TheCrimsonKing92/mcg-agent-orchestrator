using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Acceptance outcomes depend only on the supplied event history, lookups and evidence.
internal static class OwnerAcceptanceNarration
{
    public sealed record Narrated(string Sentence, string Why, string Next, string Act,
        OwnerConsoleLineSpans Spans, IReadOnlyList<string>? GoalPrefixes = null);

    public static bool TryNarrate(IReadOnlyList<OwnerConductEvent> ordered, int index,
        Func<string?, string> name, Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence,
        IReadOnlyList<OwnerAttentionObservation>? attention, out Narrated narrated)
    {
        narrated = null!;
        var source = ordered[index];
        if (source.EventKind == "acceptance")
        {
            if (Field(source, "result") == "passed")
            {
                var title = name(source.GoalId);
                narrated = new(title + ": passed its tests, landing next", "The required checks passed.",
                    "The conductor will land this work.", "No. You can let the conductor continue.",
                    SingleTitle(title, 0) with { OutcomeStart = title.Length + 2, OutcomeLength = "passed".Length });
                return true;
            }
            if (Field(source, "result") is not ("failed" or "blocked")) return false;
            narrated = Result(ordered, index, [source.GoalId], false, name, evidence, attention);
            return true;
        }
        if (source.EventKind != "acceptance-cohort") return false;
        var members = Members(source);
        if (members.Length < 2) return false;
        if (Head(source) == "ACCEPTANCE_COHORT_CHILD_COMPLETED")
        {
            if (ordered.Skip(index + 1).Any(value => value.EventKind == "acceptance-cohort" &&
                Members(value).Select(Prefix).Order().SequenceEqual(members.Select(Prefix).Order()) &&
                Field(value, "outcome") is "failed" or "passed")) return false;
            if (Field(source, "verdict") is not ("failed" or "passed")) return false;
        }
        else if (Field(source, "outcome") is not ("failed" or "passed" or "interaction-only")) return false;
        narrated = Result(ordered, index, members, true, name, evidence, attention);
        return true;
    }

    private static Narrated Result(IReadOnlyList<OwnerConductEvent> ordered, int index, string?[] ids, bool joint,
        Func<string?, string> name, Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence,
        IReadOnlyList<OwnerAttentionObservation>? attention)
    {
        var source = ordered[index];
        var passed = Field(source, "outcome") == "passed" || Field(source, "verdict") == "passed";
        var named = GoalList(joint ? "Joint test run for " : "", joint ? ids.Select(name).ToArray() : [name(source.GoalId)]);
        var label = named.Text;
        var spans = named.Spans with { OutcomeStart = label.Length + 2, OutcomeLength = (passed ? "passed" : "failed").Length };
        var goalPrefixes = joint && ids.Length > 1 ? ids.Select(Prefix).ToArray() : null;
        if (passed) return new(label + ": passed", "The joint checks passed.", "The conductor will land the work.",
            "No. You can let the conductor continue.", spans, goalPrefixes);
        var follow = ordered.Skip(index + 1).TakeWhile(value =>
            !(joint ? value.EventKind == source.EventKind && Members(value).Length > 1 &&
                Field(value, "outcome") is "failed" or "passed" :
                value.EventKind == "acceptance" && Prefix(value.GoalId) == Prefix(source.GoalId) &&
                Field(value, "result") is "failed" or "passed" or "blocked"))
            .Where(value => ids.Any(id => Prefix(id) == Prefix(value.GoalId))).ToArray();
        var facts = evidence?.Invoke(source);
        var attributed = Field(source, "attribution") is "FirstMemberFailed" or "SecondMemberFailed" or "BothMembersFailed" || facts?.OwnTest == true;
        var ownerQuestion = attention?.Any(entry => entry.ResolvedAt is null &&
            ids.Any(id => Prefix(id) == Prefix(entry.Question.GoalId))) == true;
        var next = NextStep(source, follow, attributed, ownerQuestion);
        var test = Field(source, "tests") ?? facts?.Tests.FirstOrDefault();
        var reason = Reason(source, facts, attributed, test);
        var sentence = label + (joint ? ": failed (" : ": failed its tests (") + reason + "); " + next;
        var why = reason + ".";
        if (joint && test is not null && !attributed) why += " Failed test: " + Words(test, "the recorded check") + ".";
        if (facts?.Checks.Count > 0) why += " Failed check: " + Words(facts.Checks[0], "the recorded check") + ".";
        return new(sentence, why, next + ".", ownerQuestion ?
            "Yes. Review the failed check and answer the question in DECISIONS." : next == "needs you" ?
            "Yes. Review the failed check and conductor status." : "No. The conductor is handling the next step.", spans, goalPrefixes);
    }

    private static string NextStep(OwnerConductEvent source, IReadOnlyList<OwnerConductEvent> follow,
        bool attributed, bool ownerQuestion)
    {
        var developer = follow.Any(value => value.EventKind == "goal-lifecycle" &&
            (Head(value) == "TaskDispatched" && Role(value) == "Developer" || Head(value) == "TaskFailed" && Field(value, "outcome") == "finding"));
        return ownerQuestion ? "needs you" :
            developer ? "sent back to the Developer" :
            follow.Any(value => Field(value, "result") is "held" or "retrying" or "started" ||
                value.EventKind == "infrastructure-deferral") || Field(source, "result") == "retrying" ? "retrying automatically" :
            attributed || Field(source, "next") == "developer" ? "sent back to the Developer" :
            "awaiting the conductor's next step";
    }

    private static string Reason(OwnerConductEvent source, OwnerActivityTestEvidence? facts, bool own, string? test)
    {
        var partitions = Field(source, "partitions");
        var interaction = Field(source, "outcome") == "interaction-only" || Field(source, "attribution") == "InteractionOnly";
        var unrelated = !interaction && (Field(source, "reason")?.Contains("flak", StringComparison.OrdinalIgnoreCase) == true ||
            partitions is not null && partitions.Split(',').All(value => value.EndsWith(":Passed", StringComparison.OrdinalIgnoreCase)));
        var checks = Field(source, "checks") ?? facts?.Checks.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(facts?.FirstFailure) ? facts.FirstFailure : interaction ? "the changes failed when tested together" : unrelated ? "an unrelated flaky test failed" : own ?
            test is not null ? "its own new test " + Words(test, "check") + " failed" : "its own checks failed" :
            Field(source, "type") == "timeout" ? "the test run timed out" : Field(source, "stage") switch
            {
                "rebase" => "the changes could not be updated to main",
                "merge" => "the changes could not be added to main",
                "source-size-preflight" => "a source file exceeded its size limit",
                _ => checks is not null ? "the check " + Words(checks.Replace('_', ' '), "verification") + " failed" :
                    test is not null ? Words(test, "") + " failed" : FailureReason(source) is { } rejection ? "plan rejected: " + rejection :
                    "the failure reason has not been recorded"
            };
    }
}
