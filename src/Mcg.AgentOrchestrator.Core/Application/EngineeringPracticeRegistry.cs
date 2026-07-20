using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record EngineeringPracticeProvenance(
    string ReceiptId,
    string Summary);

public sealed record EngineeringPractice(
    string Id,
    string Name,
    string Constraint,
    string ReviewerCheck,
    IReadOnlyList<string> ScopePatterns,
    IReadOnlyList<EngineeringPracticeProvenance> Provenance,
    int Priority = 0,
    bool IsEnabled = true);

public sealed record EngineeringPracticeMatch(
    EngineeringPractice Practice,
    int Score,
    IReadOnlyList<string> MatchedPatterns);

public static class EngineeringPracticeDefaults
{
    public static IReadOnlyList<EngineeringPractice> SeedEntries { get; } =
    [
        new(
            "classifier-positive-evidence",
            "Positive-evidence classification",
            "Classify outcomes only from positive provider/fault evidence; add decision-table tests for happy and fault paths.",
            "Verify classification is keyed to positive evidence, not breadcrumbs or absence; missing fault-path decision-table coverage is a finding.",
            ["classifier", "classification", "fault classifier", "RepositoryChangeClassifier", "DiscordOperatorFaultClassifier", "TaskOutcomeClassifier"],
            [
                new EngineeringPracticeProvenance("b0807c0e", "manufactured-failure family"),
                new EngineeringPracticeProvenance("dc26b35d", "evidence-first publication outcome")
            ],
            Priority: 100),
        new(
            "process-dispatch-hygiene",
            "Process/dispatch hygiene",
            "For process or dispatch work, require liveness checks, async output drains, no inherited handles, and explicit exit-artifact contracts.",
            "Verify child-process and dispatch code drains output asynchronously, avoids inherited handles, checks liveness, and records exit artifacts.",
            ["process/dispatch", "child process", "dispatch", "WorkerProfileDispatcher", "Processes/", "LastDispatch"],
            [
                new EngineeringPracticeProvenance("c86de253", "phantom watch liveness gap"),
                new EngineeringPracticeProvenance("ChaosGate-Bad-fd", "standard child-process handle hygiene")
            ],
            Priority: 90),
        new(
            "concurrency-event-gates",
            "Event-gated concurrency tests",
            "Concurrency and timing tests must use event gates or deterministic signals, never wall-clock pacing such as Thread.Sleep.",
            "Flag Thread.Sleep-style pacing in concurrency/timing tests unless it is guarded by deterministic event signals.",
            ["concurrency", "timing", "Thread.Sleep", "event gate", "event-gate", "race"],
            [new EngineeringPracticeProvenance("ce357df3", "timing flake from wall-clock pacing")],
            Priority: 80),
        new(
            "operator-ritual-preconditions",
            "Operator ritual preconditions",
            "When automating operator rituals, carry the documented preconditions into code and verification, including restart or publish handoff requirements.",
            "Verify automation preserves the documented operator preconditions instead of assuming the ritual happened out of band.",
            ["operator ritual", "automation", "precondition", "restart", "publish", "handoff"],
            [new EngineeringPracticeProvenance("007655e0", "stale publish handoff")],
            Priority: 70),
        new(
            "state-machine-loud-failure",
            "Loud state-machine failure",
            "State machines and gates must fail loudly on impossible or unsafe transitions rather than silently holding stale state.",
            "Flag silent holds or swallowed write/preflight failures in state machines, gates, and cancellation paths.",
            ["state machine", "preflight", "cancel-dispatch", "cancel dispatch", "silent hold", "gate"],
            [
                new EngineeringPracticeProvenance("f04e80c8", "silent preflight"),
                new EngineeringPracticeProvenance("15cf8a74", "cancel-dispatch lock")
            ],
            Priority: 60)
    ];
}

public static class EngineeringPracticeRegistryMatcher
{
    public const int DefaultMaxMatches = 4;

    public static IReadOnlyList<EngineeringPracticeMatch> Match(
        IEnumerable<EngineeringPractice> practices,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<string>? changedFiles = null,
        string? extraScopeText = null,
        int maxMatches = DefaultMaxMatches)
    {
        ArgumentNullException.ThrowIfNull(practices);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(task);

        if (maxMatches <= 0)
        {
            return [];
        }

        var fields = BuildScopeFields(goal, task, changedFiles, extraScopeText);
        return practices
            .Where(practice => practice.IsEnabled)
            .Select(practice => Score(practice, fields))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Practice.Priority)
            .ThenBy(match => match.Practice.Name, StringComparer.Ordinal)
            .Take(maxMatches)
            .ToList();
    }

    private static EngineeringPracticeMatch Score(EngineeringPractice practice, IReadOnlyList<ScopeField> fields)
    {
        var score = practice.Priority;
        var matches = new List<string>();
        foreach (var pattern in practice.ScopePatterns.Where(pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            var matchedWeight = fields
                .Where(field => PatternMatches(pattern, field.Value))
                .Select(field => field.Weight)
                .DefaultIfEmpty(0)
                .Max();
            if (matchedWeight <= 0)
            {
                continue;
            }

            score += matchedWeight;
            matches.Add(pattern.Trim());
        }

        return new EngineeringPracticeMatch(practice, matches.Count == 0 ? 0 : score, matches);
    }

    private static IReadOnlyList<ScopeField> BuildScopeFields(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<string>? changedFiles,
        string? extraScopeText)
    {
        var fields = new List<ScopeField>
        {
            new(goal.Objective, 12),
            new(task.Description, 12)
        };

        if (goal.RefinedSpec is { } spec)
        {
            fields.Add(new(spec.BehavioralContract, 10));
            fields.AddRange(spec.AcceptanceCriteria.Select(criterion => new ScopeField(criterion, 8)));
            fields.AddRange(spec.Decisions.Select(decision => new ScopeField($"{decision.Question} {decision.Choice} {decision.Rationale}", 6)));
        }

        if (changedFiles is not null)
        {
            fields.AddRange(changedFiles.Select(path => new ScopeField(path.Replace('\\', '/'), 20)));
        }

        if (!string.IsNullOrWhiteSpace(extraScopeText))
        {
            fields.Add(new(extraScopeText, 10));
        }

        return fields;
    }

    private static bool PatternMatches(string pattern, string value)
    {
        var normalizedPattern = Normalize(pattern);
        var normalizedValue = Normalize(value);
        if (normalizedPattern.Length == 0 || normalizedValue.Length == 0)
        {
            return false;
        }

        if (normalizedPattern.Contains('*', StringComparison.Ordinal))
        {
            var regex = "^" + Regex.Escape(normalizedPattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            return Regex.IsMatch(normalizedValue, regex, RegexOptions.CultureInvariant);
        }

        return normalizedValue.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Trim().Replace('\\', '/');

    private sealed record ScopeField(string Value, int Weight);
}

public static class EngineeringPracticePromptRenderer
{
    public static IReadOnlyList<string> RenderBriefSection(AgentRole role, IReadOnlyList<EngineeringPracticeMatch> matches)
    {
        if (matches.Count == 0)
        {
            return [];
        }

        return role switch
        {
            AgentRole.Developer => RenderWithReceipt("## PRACTICES", matches, item => item.Practice.Constraint),
            AgentRole.Reviewer => RenderWithReceipt(
                "## PRACTICES REVIEW CHECKLIST",
                matches,
                item => $"{item.Practice.ReviewerCheck} Violation is a finding named \"{item.Practice.Name}\"."),
            _ => []
        };
    }

    public static string RenderPlainTextSection(AgentRole role, IReadOnlyList<EngineeringPracticeMatch> matches)
    {
        var lines = RenderBriefSection(role, matches);
        return lines.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> RenderWithReceipt(
        string heading,
        IReadOnlyList<EngineeringPracticeMatch> matches,
        Func<EngineeringPracticeMatch, string> body)
    {
        var lines = new List<string>
        {
            heading,
            BuildReceiptLine(matches, deltaChars: 0)
        };
        lines.AddRange(matches.Select(item =>
            $"- [{item.Practice.Id}] {item.Practice.Name} ({FormatReceipts(item.Practice.Provenance)}): {body(item)}"));
        lines.Add(string.Empty);

        for (var i = 0; i < 3; i++)
        {
            var delta = string.Join(Environment.NewLine, lines).Length;
            lines[1] = BuildReceiptLine(matches, delta);
        }

        return lines;
    }

    private static string BuildReceiptLine(IReadOnlyList<EngineeringPracticeMatch> matches, int deltaChars) =>
        $"Matched practices: {matches.Count}; cap {EngineeringPracticeRegistryMatcher.DefaultMaxMatches}; prompt-size delta +{deltaChars} chars.";

    private static string FormatReceipts(IReadOnlyList<EngineeringPracticeProvenance> provenance) =>
        "receipts " + string.Join(", ", provenance.Select(item => item.ReceiptId));
}
