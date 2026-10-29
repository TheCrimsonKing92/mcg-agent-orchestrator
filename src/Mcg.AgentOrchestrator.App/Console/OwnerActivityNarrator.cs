using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;

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
        "program-health" or "acceptance" or "acceptance-cohort" or "canary-gate" or "loop-relaunch" or "loop-handoff" or
            "loop-start" or "loop-stop" or "goal-escalation" or "goal-stalled" or "train-receipt-released" or
            "owner-question-resolved" or "owner-hold-cleared" or "admission" or "infrastructure-deferral" or "experiment-reading-due" or "test-impact-headroom-low" or "provider-hold-placed" or "provider-hold-probe" or "provider-hold-restored" => true,
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
        string[] mostRecentLandingPrefixes = [];
        var lifecycle = new OwnerConductorLifecycleNarration();
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
                mostRecentLandingPrefixes = goals.Select(Prefix).ToArray();
                var landing = GoalList(goals.Length > 1 ? "Landed together: " : "Landed: ",
                    mostRecentLanding, goals.Select(id => $" ({Prefix(id)})").ToArray());
                Add(item, landing.Text,
                    "The work passed its checks and was added to main.", "The conductor will continue with the remaining work.",
                    spans: landing.Spans, goalPrefixes: goals.Length > 1 ? mostRecentLandingPrefixes : null);
                continue;
            }
            switch (item.EventKind)
            {
                case "goal-lifecycle":
                    if (DetailPhrase(item, name, finding?.Invoke(item)) is { } stage)
                        Add(item, OwnerActivityAttempts.Label(stage, DetailLead(item)!.Length + name.Length + (head == "TaskFailed" ? " back".Length : 0),
                            OwnerActivityAttempts.Number(ordered, index)), FailureReason(item) is { } rejection ? "plan rejected: " + rejection : head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
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
                case "test-impact-headroom-low":
                    if (OwnerTestImpactHeadroomNarration.TryNarrate(item, out var headroomSentence, out var headroomWhy, out var headroomNext, out var headroomAct)) Add(item, headroomSentence, headroomWhy, headroomNext, headroomAct);
                    break;
                case "provider-hold-placed" or "provider-hold-probe" or "provider-hold-restored":
                    if (OwnerProviderHoldNarration.TryNarrate(item, out var holdSentence, out var holdWhy, out var holdNext, out var holdAct))
                        Add(item, holdSentence, holdWhy, holdNext, holdAct);
                    break;
                case "experiment-reading-due":
                    if (OwnerExperimentReadingNarration.TryNarrate(item, out var readingSentence, out var readingWhy, out var readingNext, out var readingAct))
                        Add(item, readingSentence, readingWhy, readingNext, readingAct);
                    break;
                case "acceptance" or "acceptance-cohort":
                    if (OwnerAcceptanceNarration.TryNarrate(ordered, index, Name, evidence, attention, out var accepted))
                        Add(item, accepted.Sentence, accepted.Why, accepted.Next, accepted.Act, accepted.Spans, accepted.GoalPrefixes);
                    break;
                default:
                    if (lifecycle.TryNarrate(ordered, index, out var sentence, out var why, out var nextStep, out var act))
                        Add(item, sentence, why, nextStep, act);
                    else if (OwnerMainHealthNarration.TryNarrate(item, Name, evidence,
                        mostRecentLanding, mostRecentLandingPrefixes, out var health))
                        Add(item, health.Sentence, health.Why, health.Next, health.Act, health.Spans, health.GoalPrefixes);
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
            OwnerConsoleLineSpans? spans = null, IReadOnlyList<string>? goalPrefixes = null)
        {
            result.Add(new(source.Timestamp, source.EventKind, "outcome", Prefix(source.GoalId), source.Detail,
                FullName(source.GoalId), sentence, why, next, act, why, Resolution: OwnerActivityProgress.RetryAfter(source, ordered),
                Spans: spans, GoalPrefixes: goalPrefixes));
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

    internal static OwnerConsoleLineSpans SingleTitle(string title, int start) => new([TitleSpan(title, start)]);

    internal static (string Text, OwnerConsoleLineSpans Spans) GoalList(
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
        var act = decision is not null ? $"Yes. Open DECISIONS row [{decision.Number}] {decision.GoalPrefix} {decision.Kind}: {decision.Summary}" :
            $"No. {char.ToUpperInvariant(resolution[0])}{resolution[1..]}.";
        var happened = Line(item);
        if (item.GoalTitle.Length > 0 && !happened.Contains(item.GoalTitle, StringComparison.Ordinal))
            happened += " (" + item.GoalTitle + ")";
        var next = decision is not null ? "Work waits for your answer." : item.Resolution?.AutomaticRetry == true ?
            "The conductor resumed this work." : item.OwnerQuestionId is not null ?
            "This question no longer requires an owner answer." : item.Next;
        var lines = new List<string>();
        if (item.GoalPrefixes is { Count: >= 2 } prefixes && item.Spans?.Titles is { } titles && titles.Count == prefixes.Count)
        {
            var end = titles[^1].Start + titles[^1].Length + titles[^1].TrailingLength;
            lines.Add($"What happened: {item.Timestamp.ToLocalTime():HH:mm:ss} " +
                item.Phrase[..titles[0].Start] + $"{prefixes.Count} goals" + item.Phrase[end..]);
            for (var i = 0; i < prefixes.Count; i++)
                lines.Add("  " + prefixes[i] + " " + item.Phrase.Substring(titles[i].Start, titles[i].Length));
        }
        else lines.Add("What happened: " + happened);
        lines.AddRange([
            "Why: " + (item.Subject ?? item.Why),
            "What happens next: " + (decision is not null || item.Resolution is not null || item.OwnerQuestionId is not null ? resolution + ". " : "") + next,
            "Do you need to act: " + act]);
        return string.Join("\n", lines);
    }

    internal static string? FailureReason(OwnerConductEvent item)
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

    private static bool SameTrain(OwnerConductEvent left, OwnerConductEvent right) => Field(left, "tick") is { } value
        ? value == Field(right, "tick") : left.Timestamp == right.Timestamp;
    private static bool ClearsHold(OwnerConductEvent item) => item.EventKind is "owner-hold-cleared" or "train-receipt-released" ||
        item.EventKind == "goal-lifecycle" && Head(item) is "TaskDispatched" or "TaskCompleted" || IsLanding(item);
    internal static string Role(OwnerConductEvent item) => Field(item, "role") is "Author" or "Planner" or "Researcher" or "Developer" or "Tester" or "Reviewer"
        ? Field(item, "role")! : "Worker";
}

internal sealed record OwnerActivityTestEvidence(IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, bool OwnTest = false,
    string? FirstFailure = null);
