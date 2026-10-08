using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A pure chronological fold: the sentence and its explanation are one owner outcome.
internal static class OwnerActivityNarrator
{
    internal static bool Maps(OwnerConductEvent item) => item.EventKind switch
    {
        "goal-lifecycle" => Head(item) is "TaskDispatched" or "TaskCompleted" or "TaskFailed" or
            "HumanInputReceived" or "HumanInputSuperseded",
        "state-log-divergence" => Positive(item, "lost") || Positive(item, "repeated"),
        "author" => Field(item, "kind") == "ask-owner",
        "acceptance" or "acceptance-cohort" or "canary-gate" or "loop-relaunch" or "loop-handoff" or
            "loop-start" or "loop-stop" or "goal-escalation" or "goal-stalled" or "train-receipt-released" or
            "owner-question-resolved" or "owner-hold-cleared" => true,
        _ => false
    };

    internal static bool IsLanding(OwnerConductEvent item) => item.GoalId is not null &&
        item.EventKind == "loop-relaunch" && Head(item) is "LOOP_RELAUNCH_SCHEDULED" or "LOOP_RELAUNCH_NOT_REQUIRED";

    internal static int LandedToday(IEnumerable<OwnerConductEvent> events, TimeProvider clock) => events
        .Where(IsLanding).Where(item => TimeZoneInfo.ConvertTime(item.Timestamp, clock.LocalTimeZone).Date ==
            clock.GetLocalNow().Date).Distinct().Count();

    internal static IReadOnlyList<OwnerConsoleActivityItem> Narrate(IEnumerable<OwnerConductEvent> events,
        Func<string?, string> title, Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence = null)
    {
        var ordered = events.Where(Maps).Distinct().OrderBy(item => item.Timestamp).ToArray();
        var result = new List<OwnerConsoleActivityItem>();
        var questions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var landed = new HashSet<OwnerConductEvent>();
        var mostRecentLanding = "recent work";
        var restarting = false;
        var stopped = false;
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            var prefix = Prefix(item.GoalId);
            var name = Name(item.GoalId);
            var head = Head(item);
            if (held.Contains(prefix) && ClearsHold(item))
            {
                held.Remove(prefix);
                Add(item, "Moving again: " + name, "The earlier hold has cleared.", "Work can continue.");
            }
            if (IsLanding(item))
            {
                if (!landed.Add(item)) continue;
                // Group one contiguous train; a process restart can reuse a loop number.
                var train = new List<OwnerConductEvent> { item };
                for (var next = index + 1; next < ordered.Length && IsLanding(ordered[next]) &&
                    SameTrain(item, ordered[next]); next++)
                    if (landed.Add(ordered[next])) train.Add(ordered[next]);
                var goals = train.Select(value => value.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                mostRecentLanding = string.Join(", ", goals.Select(Name));
                Add(item, (goals.Length > 1 ? "Landed together: " : "Landed: ") +
                    string.Join(", ", goals.Select(id => $"{Name(id)} ({Prefix(id)})")),
                    "The work passed its checks and was added to main.", "The conductor will continue with the remaining work.");
                continue;
            }
            switch (item.EventKind)
            {
                case "goal-lifecycle":
                    if (head is "HumanInputReceived" or "HumanInputSuperseded") Resolve(item);
                    else if (DetailPhrase(item, name) is { } stage)
                        Add(item, stage, head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
                            "The worker reported a blocking finding." : "The worker could not finish its task." :
                            "The work reached this stage.", head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
                            "The Developer will address the finding." : "The conductor will retry the worker or request help." : "Work continues through the remaining checks.");
                    break;
                case "owner-question-resolved": Resolve(item); break;
                case "author":
                case "goal-escalation":
                    if (head == "ownerless-hold-stalled") { if (!held.Contains(prefix)) Stall(item); break; }
                    var question = Words(Field(item, "question"), head == "owner-review-hold" ?
                        "Approve the completed work?" : "Review the blocked work and choose how to proceed.");
                    if (questions.GetValueOrDefault(prefix) == question) break;
                    questions[prefix] = question;
                    resolved.Remove(prefix);
                    Add(item, "Needs you: " + question, "The conductor needs an owner decision before continuing.",
                        "Work waits for your answer.", "Yes. Open the question in DECISIONS and answer it.");
                    break;
                case "goal-stalled": Stall(item); break;
                case "acceptance":
                    if (Field(item, "result") == "passed")
                        Add(item, name + ": passed its tests, landing next", "The required checks passed.", "The conductor will land this work.");
                    else if (Field(item, "result") is "failed" or "blocked") TestResult(item, [item.GoalId], false);
                    break;
                case "acceptance-cohort":
                    var members = Members(item);
                    if (members.Length < 2) break;
                    if (head == "ACCEPTANCE_COHORT_CHILD_COMPLETED")
                    {
                        if (ordered.Skip(index + 1).Any(value => value.EventKind == "acceptance-cohort" &&
                            Members(value).Select(Prefix).Order().SequenceEqual(members.Select(Prefix).Order()) &&
                            Field(value, "outcome") is "failed" or "passed")) break;
                        if (Field(item, "verdict") is not ("failed" or "passed")) break;
                    }
                    else if (Field(item, "outcome") is not ("failed" or "passed" or "interaction-only")) break;
                    TestResult(item, members, true);
                    break;
                case "canary-gate":
                    if (Field(item, "result") == "failed")
                    {
                        var checks = evidence?.Invoke(item)?.Checks;
                        var check = Words(Field(item, "tests") ?? checks?.FirstOrDefault(), "the full test run on main");
                        Add(item, $"Main broke after landing {mostRecentLanding}: {check}",
                            "A check of main failed after the work landed.", "Further landings wait while the failure is investigated.",
                            "Yes. Review the failure and decide how to repair main.");
                    }
                    break;
                case "loop-handoff":
                    if (head is "ACTIVATION_REVERTED" or "ACTIVATION_FAILED_BOTH" or "LOOP_HANDOFF_FAILED")
                    {
                        var reason = RestartReason(item);
                        // FAILED_BOTH supplies no evidence that the old process remains usable.
                        if (head == "ACTIVATION_FAILED_BOTH" || head == "LOOP_HANDOFF_FAILED" &&
                            (Field(item, "continuing") != "true" || stopped))
                            Add(item, "Conductor stopped", "The conductor could not start the new version (" + reason + ").",
                                "Work waits for the conductor to be started.", "Yes. Check conductor status and restart it after addressing the failure.");
                        else Add(item, "Conductor could not switch to the new code; still running the previous version (" + reason + ")",
                            "The new version could not be activated.", "The previous version continues running.",
                            "Yes. Review conductor status and address the startup failure.");
                    }
                    break;
                case "loop-stop":
                    stopped = true;
                    restarting = Field(item, "reason") == "self-relaunch-handoff" ||
                        Field(item, "reason") == "max-duration" && ordered.Skip(index + 1).Any(value =>
                            value.EventKind == "loop-start" || value.EventKind == "loop-handoff" && Head(value) == "ACTIVATION_ADOPTED");
                    if (!restarting) Add(item, "Conductor stopped", "The conductor finished or was stopped.", "Work waits until the conductor starts.");
                    break;
                case "loop-start":
                    if (!restarting && Field(item, "selfCheck") != "true")
                        Add(item, "Conductor started", "The conductor was started.", "It will continue the queued work.");
                    restarting = false;
                    stopped = false;
                    break;
                case "state-log-divergence":
                    Add(item, "Needs you: Review the goal's incomplete history.", "Some recorded progress is missing or repeated.",
                        "The goal waits for its history to be checked.", "Yes. Review the goal detail and ask the operator to repair its history.");
                    break;
            }
        }
        return result.OrderByDescending(item => item.Timestamp).Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray();

        string Name(string? id) => Words(title(id), Prefix(id).Length == 0 ? "This work" : Prefix(id));
        void Add(OwnerConductEvent source, string sentence, string why, string next, string act = "No. You can let the conductor continue.") =>
            result.Add(new(source.Timestamp, source.EventKind, act.StartsWith("Yes", StringComparison.Ordinal) ? "decision" : "outcome",
                Prefix(source.GoalId), source.Detail, Name(source.GoalId), sentence, why, next, act));
        void Resolve(OwnerConductEvent source)
        {
            var key = Prefix(source.GoalId);
            var sourceQuestion = Field(source, "question");
            if (!questions.ContainsKey(key) && resolved.ContainsKey(key) &&
                (sourceQuestion is null || Words(sourceQuestion, "") == resolved[key])) return;
            var question = Words(sourceQuestion, questions.GetValueOrDefault(key) ?? "The earlier owner question.");
            questions.Remove(key);
            resolved[key] = question;
            Add(source, "Resolved: " + question, "The question was answered or cleared.", "The conductor can continue.");
        }
        void Stall(OwnerConductEvent source)
        {
            var key = Prefix(source.GoalId);
            if (!held.Add(key)) return;
            var seconds = Field(source, "repeatedForSeconds") ?? Field(source, "heldForSeconds");
            var minutes = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                ? Math.Max(0, (int)(duration / 60)) : 0;
            Add(source, $"Waiting: {Name(source.GoalId)} has been held {minutes} min: {Blocker(Field(source, "blocker"))}",
                "The conductor reported that the work is held.", "Work resumes when the blocker clears.",
                "Check DECISIONS for an owner question; otherwise the conductor will retry when it can.");
        }
        void TestResult(OwnerConductEvent source, string?[] ids, bool joint)
        {
            var passed = Field(source, "outcome") == "passed" || Field(source, "verdict") == "passed";
            var label = joint ? "Joint test run for " + string.Join(", ", ids.Select(Name)) : Name(source.GoalId);
            if (passed) { Add(source, label + ": passed", "The joint checks passed.", "The conductor will land the work."); return; }
            var follow = ordered.Skip(Array.IndexOf(ordered, source) + 1).TakeWhile(value =>
                !(value.EventKind == source.EventKind && Members(value).Length > 1 && Field(value, "outcome") is "failed" or "passed"))
                .Where(value => ids.Any(id => Prefix(id) == Prefix(value.GoalId)) &&
                    (value.EventKind == source.EventKind || value.EventKind == "goal-escalation")).ToArray();
            var facts = evidence?.Invoke(source);
            var attributed = Field(source, "attribution") is "FirstMemberFailed" or "SecondMemberFailed" or "BothMembersFailed" || facts?.OwnTest == true;
            var next = follow.Any(value => Field(value, "result") == "escalated" || value.EventKind == "goal-escalation") ? "needs you" :
                follow.Any(value => Field(value, "result") is "held" or "retrying") || Field(source, "result") == "retrying" ? "retrying automatically" :
                attributed || Field(source, "next") == "developer" ? "sent back to the Developer" : "needs you";
            var partitions = Field(source, "partitions");
            var interaction = Field(source, "outcome") == "interaction-only" || Field(source, "attribution") == "InteractionOnly";
            var unrelated = !interaction && (Field(source, "reason")?.Contains("flak", StringComparison.OrdinalIgnoreCase) == true ||
                partitions is not null && partitions.Split(',').All(value => value.EndsWith(":Passed", StringComparison.OrdinalIgnoreCase)));
            var test = Field(source, "tests") ?? facts?.Tests.FirstOrDefault();
            var own = attributed;
            var reason = interaction ? "the changes failed when tested together" : unrelated ? "an unrelated flaky test failed" : own && test is not null ?
                "its own new test " + Words(test, "check") + " failed" : "the test machine had a problem";
            var sentence = label + (joint ? ": failed (" : ": failed its tests (") + reason + "); " + next;
            var why = reason + ".";
            if (joint && test is not null && !own) why += " Failed test: " + Words(test, "the recorded check") + ".";
            if (facts?.Checks.Count > 0) why += " Failed check: " + Words(facts.Checks[0], "the recorded check") + ".";
            Add(source, sentence, why, next + ".", next == "needs you" ?
                "Yes. Review the failed check and answer the question in DECISIONS." : "No. The conductor is handling the next step.");
        }
    }

    internal static string? DetailPhrase(OwnerConductEvent item, string title) => Head(item) switch
    {
        "TaskDispatched" => $"{Role(item)} started on {title}",
        "TaskCompleted" => $"{Role(item)} passed {title}",
        "TaskFailed" => $"{Role(item)} sent {title} back: " + (Field(item, "outcome") == "finding" ?
            "a problem needs correction" : "the worker could not finish"),
        _ => null
    };

    internal static string Line(OwnerConsoleActivityItem item) => $"{item.Timestamp.ToLocalTime():HH:mm:ss} {item.Phrase}";
    internal static string Explain(OwnerConsoleActivityItem item) => string.Join("\n", [
        "What happened: " + Line(item), "Why: " + item.Why, "What happens next: " + item.Next,
        "Do you need to act: " + item.Act]);

    internal static string Blocker(string? value) => value?.ToLowerInvariant() switch
    {
        { } text when text.Contains("approval") || text.Contains("owner-review") || text.Contains("owner review") => "waiting for your approval",
        { } text when text.Contains("question") || text.Contains("human") => "waiting for your answer",
        { } text when text.Contains("history") || text.Contains("divergence") => "the goal's history needs review",
        { } text when text.Contains("capacity") || text.Contains("slot") => "waiting for test or worker capacity",
        { } text when text.Contains("main") => "main must be repaired before landing",
        { } text when text.Contains("receipt") || text.Contains("gate") || text.Contains("test") => "waiting for verification to finish",
        _ => "a blocker needs review"
    };

    internal static string? Field(OwnerConductEvent item, string name)
    {
        var match = Regex.Match(item.Detail, @"(?:^|\s)" + Regex.Escape(name) + @"=(.*?)(?=\s+[\w-]+=|$)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
    private static string Head(OwnerConductEvent item) => item.Detail.Split(' ', 2)[0];
    private static string Prefix(string? id) => id is null ? "" : id[..Math.Min(8, id.Length)];
    private static string[] Members(OwnerConductEvent item) => (Field(item, "members") ?? "").Split([',', '+'], StringSplitOptions.RemoveEmptyEntries);
    private static bool SameTrain(OwnerConductEvent left, OwnerConductEvent right) => Field(left, "tick") is { } value
        ? value == Field(right, "tick") : left.Timestamp == right.Timestamp;
    private static bool Positive(OwnerConductEvent item, string name) =>
        Regex.Matches(item.Detail, @"(?:^|\s)" + name + @"=").Count == 1 && int.TryParse(Field(item, name), out var count) && count > 0;
    private static bool ClearsHold(OwnerConductEvent item) => item.EventKind is "owner-hold-cleared" or "train-receipt-released" ||
        item.EventKind == "goal-lifecycle" && Head(item) is "TaskDispatched" or "TaskCompleted" || IsLanding(item);
    private static string Role(OwnerConductEvent item) => Field(item, "role") is "Author" or "Planner" or "Researcher" or "Developer" or "Tester" or "Reviewer"
        ? Field(item, "role")! : "Worker";
    private static string RestartReason(OwnerConductEvent item) => Field(item, "reason")?.ToLowerInvariant() switch
    {
        { } value when value.Contains("publish") => "the new version could not be built",
        { } value when value.Contains("timeout") || value.Contains("deadline") => "the new version did not become ready",
        { } value when value.Contains("exit") => "the new process exited before becoming ready",
        _ => "the new version could not start"
    };
    private static string Words(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Keep the owner's subject and question, translating quoted implementation vocabulary.
        foreach (var (term, phrase) in new[] { ("ticket", "issue"), ("sticky", "persistent"),
            ("none", "no issue"), ("child result", "test result"), ("handoff", "switch"),
            ("cohort", "joint run"), ("receipt", "proof"), ("canary", "main check"),
            ("tick", "step"), ("eventKind", "event type") })
            text = Regex.Replace(text, Regex.Escape(term), phrase, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }
}

internal sealed record OwnerActivityTestEvidence(IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, bool OwnTest = false);
