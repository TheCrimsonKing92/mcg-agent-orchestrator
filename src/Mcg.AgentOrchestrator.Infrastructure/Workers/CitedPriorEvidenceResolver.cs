using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum CitedPriorEvidenceKind
{
    Goal,
    Task,
    Unspecified
}

internal sealed record CitedPriorEvidenceSelector(CitedPriorEvidenceKind Kind, string Token);

internal sealed record CitedPriorTaskMatch(GoalSnapshot Goal, TaskSnapshot Task);

internal interface ICitedPriorEvidenceReader
{
    IReadOnlyList<GoalSnapshot> FindGoals(string idPrefix, int limit);

    IReadOnlyList<CitedPriorTaskMatch> FindTasks(string idPrefix, int limit);
}

/// <summary>
/// Resolves only prior goal/task identifiers explicitly cited by author-controlled brief fields.
/// The worker receives the rendered artifact, never a repository handle or store path.
/// </summary>
public sealed class CitedPriorEvidenceResolver
{
    internal const int MaxCitedEntities = 4;
    internal const int MaxRoundsPerEntity = 8;
    internal const int MaxUtf8Bytes = 20_000;
    private const int LookupLimit = 2;
    private const int MaxReasonChars = 240;
    private const int MaxReceiptChars = 1_200;

    private static readonly Regex CitationPattern = new(
        "(?ix)" +
        @"(?<![0-9a-f])goal-events[/\\](?<goalpath>[0-9a-f]{8,32})\.jsonl" +
        @"|\bgoal(?:\s+id)?\s*(?:[:=#-]\s*)?`?(?<goal>[0-9a-f]{8,32})`?(?![0-9a-f])" +
        @"|\btask(?:\s+id)?\s*(?:[:=#-]\s*)?`?(?<task>[0-9a-f]{8,32})`?(?![0-9a-f])" +
        @"|(?<![0-9a-f])`(?<unspecified>[0-9a-f]{8,32})`(?![0-9a-f])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ICitedPriorEvidenceReader _reader;

    public string? OrchestratorDirectory { get; }

    internal CitedPriorEvidenceResolver(ICitedPriorEvidenceReader reader, string? orchestratorDirectory = null)
    {
        _reader = reader;
        OrchestratorDirectory = orchestratorDirectory;
    }

    public static CitedPriorEvidenceResolver ForStateDatabase(string stateDatabasePath) =>
        new(new SqliteCitedPriorEvidenceReader(stateDatabasePath));

    public static CitedPriorEvidenceResolver ForWorkspace(string stateDatabasePath, string orchestratorDirectory) =>
        new(new SqliteCitedPriorEvidenceReader(stateDatabasePath), orchestratorDirectory);

    public string? Resolve(Goal goal, TaskSpec task)
    {
        var selectors = ExtractSelectors(goal, task);
        if (selectors.Count == 0)
        {
            return null;
        }

        var orderedSelectors = selectors
            .OrderBy(selector => selector.Kind == CitedPriorEvidenceKind.Unspecified ? 1 : 0)
            .ToArray();
        var selected = orderedSelectors.Take(MaxCitedEntities).ToArray();
        var headerLines = new List<string>
        {
            "# Cited Prior Goal And Task Evidence",
            string.Empty,
            "Historical classifier receipts are reproduced as stored; they are not reclassified using current code.",
            "Citation selection order: explicit goal/task citations first, then bare identifiers; encounter order breaks ties.",
            $"Limits: {MaxCitedEntities} cited entities, {MaxRoundsPerEntity} newest rounds per entity, and {MaxUtf8Bytes:N0} UTF-8 bytes total. Oldest rounds are omitted first.",
            $"Measurement marker: prior_evidence_package=v1; cited_entities={selectors.Count}; packaged_entities={selected.Length}.",
            string.Empty
        };

        if (selectors.Count > selected.Length)
        {
            var omitted = string.Join(", ", orderedSelectors.Skip(selected.Length).Select(DescribeSelector));
            headerLines.Add($"> Truncated cited entities: {selectors.Count - selected.Length} omitted after relevance ordering. Omitted citations: {omitted}.");
            headerLines.Add(string.Empty);
        }

        var sections = new List<CitedEvidenceSection>();
        foreach (var selector in selected)
        {
            try
            {
                sections.Add(ResolveSelector(selector));
            }
            catch (Exception ex)
            {
                sections.Add(BuildUnavailable(selector, $"historical store lookup failed ({ex.GetType().Name}: {Sanitize(ex.Message, MaxReasonChars)})"));
            }
        }

        return RenderBounded(headerLines, sections);
    }

    internal static IReadOnlyList<CitedPriorEvidenceSelector> ExtractSelectors(Goal goal, TaskSpec task)
    {
        var fields = new List<string?>
        {
            goal.Objective,
            goal.RefinedSpec?.BehavioralContract
        };
        if (goal.RefinedSpec is not null)
        {
            fields.AddRange(goal.RefinedSpec.AcceptanceCriteria);
        }

        fields.Add(task.Description);
        fields.Add(task.VerificationPlan);

        var selectors = new List<CitedPriorEvidenceSelector>();
        var selectorIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (Match match in CitationPattern.Matches(field!))
            {
                var (kind, token) = ReadMatch(match);
                if (string.IsNullOrEmpty(token) || IsCurrentIdentifier(token, goal.Id.Value, task.Id.Value))
                {
                    continue;
                }

                token = token.ToLowerInvariant();
                if (selectorIndexes.TryGetValue(token, out var existingIndex))
                {
                    if (selectors[existingIndex].Kind == CitedPriorEvidenceKind.Unspecified &&
                        kind != CitedPriorEvidenceKind.Unspecified)
                    {
                        selectors[existingIndex] = new CitedPriorEvidenceSelector(kind, token);
                    }

                    continue;
                }

                selectorIndexes[token] = selectors.Count;
                selectors.Add(new CitedPriorEvidenceSelector(kind, token));
            }
        }

        return selectors;
    }

    private CitedEvidenceSection ResolveSelector(CitedPriorEvidenceSelector selector)
    {
        return selector.Kind switch
        {
            CitedPriorEvidenceKind.Goal => ResolveGoalLookup(selector),
            CitedPriorEvidenceKind.Task => ResolveTaskLookup(selector),
            _ => ResolveUnspecifiedLookup(selector)
        };
    }

    private CitedEvidenceSection ResolveGoalLookup(CitedPriorEvidenceSelector selector)
    {
        var matches = _reader.FindGoals(selector.Token, LookupLimit);
        if (matches.Count == 0)
        {
            return BuildUnavailable(selector, "no goal id matched the cited prefix");
        }

        if (matches.Count > 1)
        {
            return BuildUnavailable(selector, "the cited goal prefix is ambiguous");
        }

        return BuildGoalEvidence(selector, matches[0], taskFilter: null);
    }

    private CitedEvidenceSection ResolveTaskLookup(CitedPriorEvidenceSelector selector)
    {
        var matches = _reader.FindTasks(selector.Token, LookupLimit);
        if (matches.Count == 0)
        {
            return BuildUnavailable(selector, "no task id matched the cited prefix");
        }

        if (matches.Count > 1)
        {
            return BuildUnavailable(selector, "the cited task prefix is ambiguous");
        }

        return BuildGoalEvidence(selector, matches[0].Goal, matches[0].Task.Id);
    }

    private CitedEvidenceSection ResolveUnspecifiedLookup(CitedPriorEvidenceSelector selector)
    {
        var goals = _reader.FindGoals(selector.Token, LookupLimit);
        var tasks = _reader.FindTasks(selector.Token, LookupLimit);
        if (goals.Count + tasks.Count == 0)
        {
            return BuildUnavailable(selector, "no goal or task id matched the cited selector");
        }

        if (goals.Count + tasks.Count > 1)
        {
            return BuildUnavailable(selector, "the cited selector is ambiguous across goal/task records");
        }

        return goals.Count == 1
            ? BuildGoalEvidence(selector, goals[0], taskFilter: null)
            : BuildGoalEvidence(selector, tasks[0].Goal, tasks[0].Task.Id);
    }

    private static CitedEvidenceSection BuildGoalEvidence(
        CitedPriorEvidenceSelector selector,
        GoalSnapshot goal,
        string? taskFilter)
    {
        var kind = taskFilter is null ? "goal" : "task";
        var preamble = new List<string>
        {
            $"## Cited {kind} `{selector.Token}`",
            string.Empty,
            $"Resolved goal: `{goal.Id}`."
        };
        if (taskFilter is not null)
        {
            preamble.Add($"Resolved task: `{taskFilter}`.");
        }

        var taskIds = taskFilter is null
            ? goal.Tasks.Select(candidate => candidate.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([taskFilter], StringComparer.OrdinalIgnoreCase);
        var rounds = goal.Tasks
            .Where(candidate => taskIds.Contains(candidate.Id))
            .SelectMany(candidate => BuildRounds(goal, candidate))
            .OrderBy(round => round.Timestamp)
            .ToArray();
        var shown = rounds.TakeLast(MaxRoundsPerEntity).ToArray();

        preamble.Add($"Rounds: {rounds.Length}; showing newest {shown.Length}.");
        if (rounds.Length > shown.Length)
        {
            preamble.Add($"> Truncated rounds: {rounds.Length - shown.Length} older rounds omitted.");
        }

        if (shown.Length == 0)
        {
            preamble.Add("- No verification or classifier receipt records were available.");
        }

        var roundBlocks = shown.Select(round =>
            $"- task={round.TaskId}; timestamp={round.Timestamp:O}; {round.Summary}{Environment.NewLine}" +
            $"  classifier_receipt: {round.ClassifierReceipt ?? "unavailable (none stored for this round)"}").ToArray();
        return new CitedEvidenceSection(selector, preamble, roundBlocks);
    }

    private static IReadOnlyList<CitedRound> BuildRounds(GoalSnapshot goal, TaskSnapshot task)
    {
        var verificationSource = task.VerificationHistory is { Count: > 0 }
            ? task.VerificationHistory
            : task.LastVerification is null
                ? []
                : [task.LastVerification];
        var verifications = verificationSource
            .OrderBy(record => record.CompletedAt)
            // BuildRounds is task-scoped, so completion time is the stable identity of a stored round.
            .DistinctBy(record => record.CompletedAt)
            .ToArray();
        var receipts = goal.Timeline
            .Where(evt => string.Equals(evt.TaskId, task.Id, StringComparison.OrdinalIgnoreCase))
            .Where(evt => evt.Kind is ProgressKind.TaskNote or ProgressKind.OperatorTaskNote)
            .Where(evt => evt.Message.Contains("CLASSIFIER ", StringComparison.OrdinalIgnoreCase))
            .OrderBy(evt => evt.OccurredAt)
            .DistinctBy(evt => new { evt.OccurredAt, evt.Message })
            .ToArray();
        var used = new bool[receipts.Length];
        var rounds = new List<CitedRound>();

        for (var index = 0; index < verifications.Length; index++)
        {
            var verification = verifications[index];
            var before = index + 1 < verifications.Length ? verifications[index + 1].CompletedAt : DateTimeOffset.MaxValue;
            var receiptIndex = -1;
            for (var candidateIndex = 0; candidateIndex < receipts.Length; candidateIndex++)
            {
                if (!used[candidateIndex] &&
                    receipts[candidateIndex].OccurredAt >= verification.CompletedAt &&
                    receipts[candidateIndex].OccurredAt < before)
                {
                    receiptIndex = candidateIndex;
                    break;
                }
            }
            ProgressEventSnapshot? receipt = null;
            if (receiptIndex >= 0)
            {
                used[receiptIndex] = true;
                receipt = receipts[receiptIndex];
            }

            rounds.Add(new CitedRound(
                task.Id,
                verification.CompletedAt,
                BuildVerificationSummary(verification),
                receipt is null ? null : Sanitize(receipt.Message, MaxReceiptChars)));
        }

        for (var index = 0; index < receipts.Length; index++)
        {
            if (!used[index])
            {
                rounds.Add(new CitedRound(
                    task.Id,
                    receipts[index].OccurredAt,
                    "verification=unavailable; worker_result=unknown; blockers=unknown",
                    Sanitize(receipts[index].Message, MaxReceiptChars)));
            }
        }

        return rounds;
    }

    private static string BuildVerificationSummary(TaskVerificationSnapshot verification)
    {
        var duration = verification.DispatchStartedAt is null
            ? "unknown"
            : Math.Max(0, (long)(verification.CompletedAt - verification.DispatchStartedAt.Value).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var workerResult = verification.WorkerResultPresent ? "present" : "missing";
        var blockers = "unknown";
        if (WorkerResultParser.TryParseResult(verification.StandardOutput, out var parsed, out _))
        {
            workerResult = "present";
            blockers = parsed.BlockersStatus switch
            {
                WorkerResultParser.BlockersStatus.None => "none",
                WorkerResultParser.BlockersStatus.Present => "present",
                _ => "unknown"
            };
        }

        return $"exit_code={verification.ExitCode}; child_exit_code={verification.ChildExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; duration_ms={duration}; worker_result={workerResult}; blockers={blockers}";
    }

    private static CitedEvidenceSection BuildUnavailable(CitedPriorEvidenceSelector selector, string reason)
    {
        return new CitedEvidenceSection(
            selector,
            [
                $"## Cited {selector.Kind.ToString().ToLowerInvariant()} `{selector.Token}`",
                string.Empty,
                $"> Records unavailable for cited {selector.Kind.ToString().ToLowerInvariant()} `{selector.Token}`: {Sanitize(reason, MaxReasonChars)}."
            ],
            []);
    }

    private static (CitedPriorEvidenceKind Kind, string Token) ReadMatch(Match match)
    {
        if (match.Groups["goalpath"].Success)
            return (CitedPriorEvidenceKind.Goal, match.Groups["goalpath"].Value);
        if (match.Groups["goal"].Success)
            return (CitedPriorEvidenceKind.Goal, match.Groups["goal"].Value);
        if (match.Groups["task"].Success)
            return (CitedPriorEvidenceKind.Task, match.Groups["task"].Value);
        return (CitedPriorEvidenceKind.Unspecified, match.Groups["unspecified"].Value);
    }

    private static bool IsCurrentIdentifier(string token, string goalId, string taskId) =>
        goalId.StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
        taskId.StartsWith(token, StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value, int maxChars)
    {
        var sanitized = value.ReplaceLineEndings(" ").Trim();
        while (sanitized.Contains("  ", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return sanitized.Length <= maxChars ? sanitized : sanitized[..(maxChars - 3)] + "...";
    }

    private static string RenderBounded(IReadOnlyList<string> headerLines, IReadOnlyList<CitedEvidenceSection> sections)
    {
        var allRoundCounts = sections.Select(section => section.RoundBlocks.Count).ToArray();
        var complete = Render(headerLines, sections, allRoundCounts, byteCapReached: false);
        if (Encoding.UTF8.GetByteCount(complete) <= MaxUtf8Bytes)
        {
            return complete;
        }

        var includedRoundCounts = new int[sections.Count];
        for (var depth = 0; depth < MaxRoundsPerEntity; depth++)
        {
            var addedAtThisDepth = false;
            for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
            {
                if (includedRoundCounts[sectionIndex] >= sections[sectionIndex].RoundBlocks.Count)
                {
                    continue;
                }

                includedRoundCounts[sectionIndex]++;
                var candidate = Render(headerLines, sections, includedRoundCounts, byteCapReached: true);
                if (Encoding.UTF8.GetByteCount(candidate) <= MaxUtf8Bytes)
                {
                    addedAtThisDepth = true;
                }
                else
                {
                    includedRoundCounts[sectionIndex]--;
                }
            }

            if (!addedAtThisDepth)
            {
                break;
            }
        }

        return Render(headerLines, sections, includedRoundCounts, byteCapReached: true);
    }

    private static string Render(
        IReadOnlyList<string> headerLines,
        IReadOnlyList<CitedEvidenceSection> sections,
        IReadOnlyList<int> includedRoundCounts,
        bool byteCapReached)
    {
        var lines = new List<string>(headerLines);
        for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            var section = sections[sectionIndex];
            lines.AddRange(section.Preamble);
            var included = includedRoundCounts[sectionIndex];
            lines.AddRange(section.RoundBlocks.Skip(section.RoundBlocks.Count - included));
            var omitted = section.RoundBlocks.Count - included;
            if (omitted > 0)
            {
                lines.Add($"> UTF-8 byte cap omission for {DescribeSelector(section.Selector)}: {omitted} resolved rounds omitted; {included} newest rounds included.");
            }

            lines.Add(string.Empty);
        }

        if (byteCapReached)
        {
            lines.Add($"> UTF-8 byte cap reached: round details were bounded at {MaxUtf8Bytes:N0} bytes; every selected citation and its exact omission count remain above.");
        }

        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    private static string DescribeSelector(CitedPriorEvidenceSelector selector) =>
        $"cited {selector.Kind.ToString().ToLowerInvariant()} `{selector.Token}`";

    private sealed record CitedEvidenceSection(
        CitedPriorEvidenceSelector Selector,
        IReadOnlyList<string> Preamble,
        IReadOnlyList<string> RoundBlocks);

    private sealed record CitedRound(
        string TaskId,
        DateTimeOffset Timestamp,
        string Summary,
        string? ClassifierReceipt);
}

internal sealed class SqliteCitedPriorEvidenceReader(string stateDatabasePath) : ICitedPriorEvidenceReader
{
    private readonly SqliteOrchestratorStateRepository _repository =
        SqliteOrchestratorStateRepository.OpenReadOnly(stateDatabasePath);

    public IReadOnlyList<GoalSnapshot> FindGoals(string idPrefix, int limit) =>
        _repository.FindGoalSnapshotsByIdPrefixAsync(idPrefix, limit).GetAwaiter().GetResult();

    public IReadOnlyList<CitedPriorTaskMatch> FindTasks(string idPrefix, int limit) =>
        _repository.FindTaskSnapshotsByIdPrefixAsync(idPrefix, limit).GetAwaiter().GetResult();
}
