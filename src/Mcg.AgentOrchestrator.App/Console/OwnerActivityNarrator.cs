using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A pure chronological fold: the sentence and its explanation are one owner outcome.
internal static class OwnerActivityNarrator
{
    internal const string JumpKeyHint = "Home/End first/last  PgUp/PgDn page";
    internal static IReadOnlyList<string> JumpKeyHelp => [
        "Home/End: Select the first/last row in DECISIONS, BOARD or ACTIVITY.",
        "PgUp/PgDn: Move the selection one visible page up/down in DECISIONS, BOARD or ACTIVITY."];

    internal static bool Maps(OwnerConductEvent item) => item.EventKind switch
    {
        "goal-lifecycle" => Head(item) is "TaskDispatched" or "TaskCompleted" or "TaskFailed" or
            "HumanInputReceived" or "HumanInputSuperseded" || Field(item, "resolution-verb") is not null,
        "state-log-divergence" => Positive(item, "lost") || Positive(item, "repeated"),
        "author" => Field(item, "kind") == "ask-owner",
        "acceptance" or "acceptance-cohort" or "canary-gate" or "loop-relaunch" or "loop-handoff" or
            "loop-start" or "loop-stop" or "goal-escalation" or "goal-stalled" or "train-receipt-released" or
            "owner-question-resolved" or "owner-hold-cleared" or "admission" or "infrastructure-deferral" => true,
        _ => false
    };

    internal static bool IsLanding(OwnerConductEvent item) => item.GoalId is not null &&
        item.EventKind == "loop-relaunch" && Head(item) is "LOOP_RELAUNCH_SCHEDULED" or "LOOP_RELAUNCH_NOT_REQUIRED";

    internal static int LandedToday(IEnumerable<OwnerConductEvent> events, TimeProvider clock) => events
        .Where(IsLanding).Where(item => TimeZoneInfo.ConvertTime(item.Timestamp, clock.LocalTimeZone).Date ==
            clock.GetLocalNow().Date).Distinct().Count();

    internal static IReadOnlyList<OwnerConsoleActivityItem> Narrate(IEnumerable<OwnerConductEvent> events,
        Func<string?, string> title, Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence = null,
        IReadOnlyList<OwnerAttentionObservation>? attention = null, Func<OwnerConductEvent, string?>? finding = null)
    {
        var ordered = events.Where(Maps).Distinct().OrderBy(item => item.Timestamp).ToArray();
        var questionRoutes = new Dictionary<OwnerConductEvent, OwnerConductEvent>();
        var foldedAuthors = new HashSet<OwnerConductEvent>();
        foreach (var observation in ordered.Where(item => item.EventKind == "goal-escalation" && Head(item) == "author-owner-question"))
        {
            var candidates = ordered.Where(item => item.EventKind == "author" && !foldedAuthors.Contains(item) &&
                Prefix(item.GoalId) == Prefix(observation.GoalId) && SameQuestion(item, observation)).ToArray();
            var route = candidates.Where(item => item.Timestamp <= observation.Timestamp).MaxBy(item => item.Timestamp) ??
                candidates.MinBy(item => item.Timestamp);
            if (route is null) continue;
            questionRoutes[observation] = route;
            foldedAuthors.Add(route);
        }
        var result = new List<OwnerConsoleActivityItem>();
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var landed = new HashSet<OwnerConductEvent>();
        string[] mostRecentLanding = [];
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
                Add(item, "Moving again: " + name, "The earlier hold has cleared.", "Work can continue.",
                    spans: SingleTitle(name, "Moving again: ".Length));
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
                mostRecentLanding = goals.Select(Name).ToArray();
                var landing = GoalList(goals.Length > 1 ? "Landed together: " : "Landed: ",
                    mostRecentLanding, goals.Select(id => $" ({Prefix(id)})").ToArray());
                Add(item, landing.Text,
                    "The work passed its checks and was added to main.", "The conductor will continue with the remaining work.",
                    spans: landing.Spans);
                continue;
            }
            switch (item.EventKind)
            {
                case "goal-lifecycle":
                    if (DetailPhrase(item, name, finding?.Invoke(item)) is { } stage)
                        Add(item, stage, FailureReason(item) is { } rejection ? "plan rejected: " + rejection : head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
                            "The worker reported a blocking finding." : "The worker could not finish its task." :
                            "The work reached this stage.", head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
                            "The Developer will address the finding." : "The conductor will retry the worker or request help." : "Work continues through the remaining checks.",
                            spans: SingleTitle(name, DetailLead(item)!.Length));
                    break;
                case "owner-question-resolved": break; // Only removal from the live read model resolves owner attention.
                case "author":
                case "goal-escalation":
                    if (foldedAuthors.Contains(item)) break;
                    if (head == "ownerless-hold-stalled") { if (!held.Contains(prefix)) Stall(item); break; }
                    var question = OwnerHoldReason.FirstLine(Field(item, "question"));
                    if (attention?.Any(entry => Prefix(entry.Question.GoalId) == prefix && question is not null &&
                        OwnerHoldReason.FirstLine(entry.Question.Text) == question) == true) break;
                    var rawReason = Field(item, "reason");
                    var authorQuestion = item.EventKind == "goal-escalation" && question is null &&
                        OwnerEscalationReasonText.IsAuthorQuestion(rawReason);
                    if (!authorQuestion && (head == "author-owner-question" || item.EventKind == "author"))
                    {
                        var routing = questionRoutes.GetValueOrDefault(item) ?? item;
                        var recipient = QuestionRecipient(Field(routing, "recipient") ?? Field(routing, "reason") ??
                            Field(item, "recipient") ?? Field(item, "reason"));
                        var lead = prefix + " question for " + recipient + (question is null ? " " : ": ");
                        var text = question is null ? "about " + name : Words(question, "");
                        Add(item, lead + text, text, "The conductor or operator will handle the next step.",
                            spans: question is null ? SingleTitle(name, lead.Length + "about ".Length) : null);
                        break;
                    }
                    var subject = FailureReason(item) is { } plan ? "plan rejected: " + plan :
                        question is not null ? Words(question, "") : head == "owner-review-hold" ? "approval of the completed work" :
                        OwnerEscalationReasonText.Plain(rawReason, "the conductor reported a hold on " + name, Words,
                            task => ordered.Take(index).LastOrDefault(value => value.EventKind == "goal-lifecycle" &&
                                Prefix(value.GoalId) == prefix && Field(value, "task")?.StartsWith(task, StringComparison.OrdinalIgnoreCase) == true)
                                is { } worker ? Role(worker) : null);
                    Add(item, prefix + (authorQuestion ? " " : " escalated: ") + subject, subject,
                        authorQuestion ? "The Author will handle the question." : "The conductor or operator will handle the next step.");
                    if (authorQuestion && ordered.Skip(index + 1).FirstOrDefault(value => value.Timestamp > item.Timestamp && value.EventKind == "goal-lifecycle" &&
                        Prefix(value.GoalId) == prefix && Field(value, "resolution-verb") == "answer" &&
                        Field(value, "resolution-actor") == "author") is { } answer)
                        result[^1] = result[^1] with { Resolution = new(answer.Timestamp, Actor: "author"),
                            Phrase = result[^1].Phrase.TrimEnd('.') + $"; answered by the Author at {answer.Timestamp.ToLocalTime():HH:mm:ss}.",
                            Next = "The Author answered the question; work can continue." };
                    break;
                case "goal-stalled": Stall(item); break;
                case "acceptance":
                    if (Field(item, "result") == "passed")
                        Add(item, name + ": passed its tests, landing next", "The required checks passed.", "The conductor will land this work.",
                            spans: SingleTitle(name, 0) with { OutcomeStart = name.Length + 2, OutcomeLength = "passed".Length });
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
                        var landingNames = GoalList("Main broke after landing ", mostRecentLanding);
                        Add(item, landingNames.Text + (mostRecentLanding.Length == 0 ? "recent work" : "") + $": {check}",
                            "A check of main failed after the work landed.", "Further landings wait while the failure is investigated.",
                            "Yes. Review the failure and decide how to repair main.", spans: landingNames.Spans);
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
                    Add(item, prefix + " history needs review: " + name, "Some recorded progress is missing or repeated.",
                        "The operator can check the goal's history.", spans: SingleTitle(name, prefix.Length + " history needs review: ".Length));
                    break;
            }
        }
        foreach (var entry in attention ?? [])
        {
            var question = Words(OwnerHoldReason.FirstLine(entry.Question.Text), "the recorded owner question");
            var source = new OwnerConductEvent(entry.FirstSeen, "owner-question", entry.Question.GoalId, "");
            Add(source, "Needs you: " + Prefix(entry.Question.GoalId) + " " + question,
                question, "Work waits for your answer.");
            var resolution = OwnerActivityProgress.Resolution(entry, ordered);
            result[^1] = result[^1] with { Tag = "decision", Subject = question, OwnerQuestionId = entry.Question.ItemId,
                Resolution = resolution };
            if (entry.ResolvedAt is { } resolvedAt)
            {
                var resolvedTitle = FullName(entry.Question.GoalId);
                var hasTitle = resolvedTitle != Prefix(entry.Question.GoalId);
                Add(source with { Timestamp = resolvedAt, EventKind = "owner-question-resolved" },
                    "Resolved: " + Prefix(entry.Question.GoalId) +
                    (hasTitle ? " " + resolvedTitle + ": " : " ") + question,
                    question, "The conductor can continue.", spans: hasTitle
                        ? SingleTitle(resolvedTitle, "Resolved: ".Length + Prefix(entry.Question.GoalId).Length + 1) : null);
                result[^1] = result[^1] with { Subject = question, OwnerQuestionId = entry.Question.ItemId,
                    Resolution = resolution, GoalId = entry.Question.GoalId, Question = entry.Question.Text };
            }
        }
        return result.OrderByDescending(item => item.Timestamp).ThenByDescending(item => item.Kind == "owner-question-resolved")
            .Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray();

        string FullName(string? id) => Words(title(id), Prefix(id).Length == 0 ? "This work" : Prefix(id));
        string Name(string? id) => FullName(id);
        void Add(OwnerConductEvent source, string sentence, string why, string next, string act = "No. You can let the conductor continue.",
            OwnerConsoleLineSpans? spans = null)
        {
            result.Add(new(source.Timestamp, source.EventKind, "outcome", Prefix(source.GoalId), source.Detail,
                FullName(source.GoalId), sentence, why, next, act, why, Resolution: OwnerActivityProgress.RetryAfter(source, ordered),
                Spans: spans));
        }
        void Stall(OwnerConductEvent source)
        {
            var key = Prefix(source.GoalId);
            if (!held.Add(key)) return;
            var seconds = Field(source, "repeatedForSeconds") ?? Field(source, "heldForSeconds");
            var minutes = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                ? Math.Max(0, (int)(duration / 60)) : 0;
            var blocker = WaitingOn(Field(source, "blocker"));
            var waitingTitle = Name(source.GoalId);
            Add(source, $"Waiting: {waitingTitle} has been held {minutes} min: {blocker}",
                blocker, "Work resumes when the blocker clears.",
                "Check DECISIONS for an owner question; otherwise the conductor will retry when it can.",
                spans: SingleTitle(waitingTitle, "Waiting: ".Length));
        }
        void TestResult(OwnerConductEvent source, string?[] ids, bool joint)
        {
            var passed = Field(source, "outcome") == "passed" || Field(source, "verdict") == "passed";
            var named = GoalList(joint ? "Joint test run for " : "", joint ? ids.Select(FullName).ToArray() : [Name(source.GoalId)]);
            var label = named.Text;
            var spans = named.Spans with { OutcomeStart = label.Length + 2, OutcomeLength = (passed ? "passed" : "failed").Length };
            if (passed) { Add(source, label + ": passed", "The joint checks passed.", "The conductor will land the work.", spans: spans); return; }
            var follow = ordered.Skip(Array.IndexOf(ordered, source) + 1).TakeWhile(value =>
                !(joint ? value.EventKind == source.EventKind && Members(value).Length > 1 &&
                    Field(value, "outcome") is "failed" or "passed" :
                    value.EventKind == "acceptance" && Prefix(value.GoalId) == Prefix(source.GoalId) &&
                    Field(value, "result") is "failed" or "passed" or "blocked"))
                .Where(value => ids.Any(id => Prefix(id) == Prefix(value.GoalId))).ToArray();
            var facts = evidence?.Invoke(source);
            var attributed = Field(source, "attribution") is "FirstMemberFailed" or "SecondMemberFailed" or "BothMembersFailed" || facts?.OwnTest == true;
            var ownerQuestion = attention?.Any(entry => entry.ResolvedAt is null &&
                ids.Any(id => Prefix(id) == Prefix(entry.Question.GoalId))) == true;
            var developer = follow.Any(value => value.EventKind == "goal-lifecycle" &&
                (Head(value) == "TaskDispatched" && Role(value) == "Developer" || Head(value) == "TaskFailed" && Field(value, "outcome") == "finding"));
            var next = ownerQuestion ? "needs you" :
                developer ? "sent back to the Developer" :
                follow.Any(value => Field(value, "result") is "held" or "retrying" or "started" ||
                    value.EventKind == "infrastructure-deferral") || Field(source, "result") == "retrying" ? "retrying automatically" :
                attributed || Field(source, "next") == "developer" ? "sent back to the Developer" :
                "awaiting the conductor's next step";
            var partitions = Field(source, "partitions");
            var interaction = Field(source, "outcome") == "interaction-only" || Field(source, "attribution") == "InteractionOnly";
            var unrelated = !interaction && (Field(source, "reason")?.Contains("flak", StringComparison.OrdinalIgnoreCase) == true ||
                partitions is not null && partitions.Split(',').All(value => value.EndsWith(":Passed", StringComparison.OrdinalIgnoreCase)));
            var test = Field(source, "tests") ?? facts?.Tests.FirstOrDefault();
            var own = attributed;
            var checks = Field(source, "checks") ?? facts?.Checks.FirstOrDefault();
            var reason = !string.IsNullOrWhiteSpace(facts?.FirstFailure) ? facts.FirstFailure : interaction ? "the changes failed when tested together" : unrelated ? "an unrelated flaky test failed" : own ?
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
            var sentence = label + (joint ? ": failed (" : ": failed its tests (") + reason + "); " + next;
            var why = reason + ".";
            if (joint && test is not null && !own) why += " Failed test: " + Words(test, "the recorded check") + ".";
            if (facts?.Checks.Count > 0) why += " Failed check: " + Words(facts.Checks[0], "the recorded check") + ".";
            Add(source, sentence, why, next + ".", ownerQuestion ?
                "Yes. Review the failed check and answer the question in DECISIONS." : next == "needs you" ?
                "Yes. Review the failed check and conductor status." : "No. The conductor is handling the next step.", spans);
        }
    }

    private static string? DetailLead(OwnerConductEvent item) => Head(item) switch
    {
        "TaskDispatched" => Role(item) + " started on ",
        "TaskCompleted" => Role(item) + " passed ",
        "TaskFailed" => Role(item) + " sent ",
        _ => null
    };

    internal static string? DetailPhrase(OwnerConductEvent item, string title, string? finding = null) => DetailLead(item) is { } lead
        ? lead + title + (Head(item) == "TaskFailed" ? " back: " + (FailureReason(item) is { } rejected
            ? "plan rejected: " + rejected : OwnerHoldReason.FirstLine(finding) ?? (Field(item, "outcome") == "finding"
                ? "a problem needs correction" : "the worker could not finish")) : "") : null;

    private static OwnerConsoleTitleSpan TitleSpan(string title, int start, int trailingLength = 0)
    {
        var colon = title.IndexOf(": ", StringComparison.Ordinal);
        return new(start, title.Length, colon < 0 ? 0 : colon + 1, trailingLength);
    }

    private static OwnerConsoleLineSpans SingleTitle(string title, int start) => new([TitleSpan(title, start)]);

    private static (string Text, OwnerConsoleLineSpans Spans) GoalList(
        string lead, IReadOnlyList<string> titles, IReadOnlyList<string>? tails = null)
    {
        var text = new StringBuilder(lead);
        var spans = new List<OwnerConsoleTitleSpan>();
        for (var i = 0; i < titles.Count; i++)
        {
            if (i > 0) text.Append(", ");
            var tail = tails?[i] ?? "";
            spans.Add(TitleSpan(titles[i], text.Length, tail.Length));
            text.Append(titles[i]).Append(tail);
        }
        return (text.ToString(), new(spans.ToArray()));
    }

    internal static string? StagePhrase(OwnerConductEvent item, string? finding = null) => Head(item) switch
    {
        "TaskDispatched" => Role(item) + " started",
        "TaskCompleted" => Role(item) + " passed",
        "TaskFailed" => Role(item) + " sent back: " + (FailureReason(item) is { } reason ? "plan rejected: " + reason :
            finding ?? (Field(item, "outcome") == "finding" ? "a problem needs correction" : "the worker could not finish")),
        _ => null
    };

    internal static string Line(OwnerConsoleActivityItem item) => $"{item.Timestamp.ToLocalTime():HH:mm:ss} " +
        (item.GoalPrefix.Length > 0 && !item.Phrase.Contains(item.GoalPrefix, StringComparison.OrdinalIgnoreCase)
            ? item.GoalPrefix + " " : "") + item.Phrase;

    internal static OwnerConsoleLineSpans LineSpans(OwnerConsoleActivityItem item) =>
        (item.Spans ?? OwnerConsoleLineSpans.None).Shift(Line(item).Length - item.Phrase.Length);

    internal static string Explain(OwnerConsoleActivityItem item, IReadOnlyList<OwnerConsoleDecision>? decisions = null)
    {
        // Match the exact item; another question on the same goal never makes this event actionable.
        var decision = item.OwnerQuestionId is null ? null : decisions?.FirstOrDefault(value =>
            value.Id == item.OwnerQuestionId && value.GoalPrefix.Equals(item.GoalPrefix, StringComparison.OrdinalIgnoreCase) &&
            Words(OwnerHoldReason.FirstLine(value.FullText), "") == item.Subject);
        var resolution = decision is not null ? "still waiting on you" : item.Resolution is { } resolved ?
            resolved.AutomaticRetry ? $"retried automatically at {resolved.At.ToLocalTime():HH:mm:ss}" :
                $"resolved at {resolved.At.ToLocalTime():HH:mm:ss}" + (resolved.Actor is { } actor ? " by " + ResolutionActor(actor) :
                    "; no longer listed in DECISIONS (the resolving actor was not recorded)") :
            item.OwnerQuestionId is not null ? "no longer listed in DECISIONS" : "no owner question is listed in DECISIONS for this event";
        var act = decision is not null ? $"Yes. open DECISIONS row [{decision.Number}] {decision.GoalPrefix} {decision.Kind}: {decision.Summary}" :
            $"No. {resolution}.";
        var happened = Line(item);
        if (item.GoalTitle.Length > 0 && !happened.Contains(item.GoalTitle, StringComparison.Ordinal))
            happened += " (" + item.GoalTitle + ")";
        var next = decision is not null ? "Work waits for your answer." : item.Resolution?.AutomaticRetry == true ?
            "The conductor resumed this work." : item.OwnerQuestionId is not null ?
            "This question no longer requires an owner answer." : item.Next;
        return string.Join("\n", ["What happened: " + happened,
            "Why: " + (item.Subject ?? item.Why),
            "What happens next: " + (decision is not null || item.Resolution is not null || item.OwnerQuestionId is not null ? resolution + ". " : "") + next,
            "Do you need to act: " + act]);
    }

    private static string? FailureReason(OwnerConductEvent item)
    {
        var reason = Field(item, "rejection") ?? OwnerPlanRejectionReason.Read(item.Detail);
        if (reason is null) return null;
        var citation = Regex.Match(reason, @"target citation '([^']+)' does not exist");
        return citation.Success ? "cited a file that does not exist: " + citation.Groups[1].Value : reason;
    }

    private static string ResolutionActor(string actor) => actor.ToLowerInvariant() switch
    {
        "author" => "the Author",
        "conductor" => "the conductor",
        "operator" or "owner" or "owner-console" or "miles" => "the operator",
        _ => actor
    };

    private static string QuestionRecipient(string? token) => token?.ToLowerInvariant() switch
    {
        { } value when value.Contains("operator", StringComparison.Ordinal) => "the operator",
        { } value when value.Contains("owner", StringComparison.Ordinal) => "you",
        _ => "the Author"
    };

    private static bool SameQuestion(OwnerConductEvent author, OwnerConductEvent observation)
    {
        var left = Field(author, "question-id") ?? Field(author, "item");
        var right = Field(observation, "question-id") ?? Field(observation, "item");
        // Author item identities include a goal prefix; question observations omit that prefix.
        if (left is not null && right is not null)
            return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
                left.Equals(author.GoalId + ":" + right, StringComparison.OrdinalIgnoreCase);
        var agent = Field(author, "agent");
        var observedAgent = Field(observation, "agent");
        return (agent is null || observedAgent is null || agent.Equals(observedAgent, StringComparison.OrdinalIgnoreCase)) &&
            author.Timestamp <= observation.Timestamp;
    }

    internal static string WaitingOn(string? value)
    {
        // Known category-only blockers have friendly labels; recorded sentences retain their subject.
        if (value?.StartsWith("waiting_for_approval", StringComparison.OrdinalIgnoreCase) == true)
            return "waiting for your approval";
        var reason = OwnerHoldReason.Read(value);
        if (reason is null) return "a blocker needs review";
        if (reason.Contains(' ') || reason.Contains('?')) return reason;
        var category = Blocker(reason);
        return category == "a blocker needs review" ? Words(reason.Replace('_', ' '), category) : category;
    }

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

internal sealed record OwnerActivityTestEvidence(IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, bool OwnTest = false,
    string? FirstFailure = null);
