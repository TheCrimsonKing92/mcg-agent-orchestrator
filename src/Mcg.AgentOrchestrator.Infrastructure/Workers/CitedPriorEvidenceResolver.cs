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

    internal CitedPriorEvidenceResolver(ICitedPriorEvidenceReader reader)
    {
        _reader = reader;
    }

    public static CitedPriorEvidenceResolver ForStateDatabase(string stateDatabasePath) =>
        new(new SqliteCitedPriorEvidenceReader(stateDatabasePath));

    public string? Resolve(Goal goal, TaskSpec task)
    {
        var selectors = ExtractSelectors(goal, task);
        if (selectors.Count == 0)
        {
            return null;
        }

        var selected = selectors.Take(MaxCitedEntities).ToArray();
        var lines = new List<string>
        {
            "# Cited Prior Goal And Task Evidence",
            string.Empty,
            "Historical classifier receipts are reproduced as stored; they are not reclassified using current code.",
            $"Limits: {MaxCitedEntities} cited entities, {MaxRoundsPerEntity} newest rounds per entity, and {MaxUtf8Bytes:N0} UTF-8 bytes total. Oldest rounds are omitted first.",
            string.Empty
        };

        if (selectors.Count > selected.Length)
        {
            lines.Add($"> Truncated cited entities: {selectors.Count - selected.Length} omitted after the first {MaxCitedEntities} distinct citations.");
            lines.Add(string.Empty);
        }

        foreach (var selector in selected)
        {
            try
            {
                AddResolvedSelector(lines, selector);
            }
            catch (Exception ex)
            {
                AddUnavailable(lines, selector, $"historical store read failed ({ex.GetType().Name}: {Sanitize(ex.Message, MaxReasonChars)})");
            }
        }

        return BoundUtf8(string.Join(Environment.NewLine, lines).TrimEnd());
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (Match match in CitationPattern.Matches(field!))
            {
                var (kind, token) = ReadMatch(match);
                if (string.IsNullOrEmpty(token) || IsCurrentIdentifier(token, goal.Id.Value, task.Id.Value) || !seen.Add(token))
                {
                    continue;
                }

                selectors.Add(new CitedPriorEvidenceSelector(kind, token.ToLowerInvariant()));
            }
        }

        return selectors;
    }

    private void AddResolvedSelector(List<string> lines, CitedPriorEvidenceSelector selector)
    {
        switch (selector.Kind)
        {
            case CitedPriorEvidenceKind.Goal:
                AddGoalLookup(lines, selector);
                break;
            case CitedPriorEvidenceKind.Task:
                AddTaskLookup(lines, selector);
                break;
            default:
                AddUnspecifiedLookup(lines, selector);
                break;
        }
    }

    private void AddGoalLookup(List<string> lines, CitedPriorEvidenceSelector selector)
    {
        var matches = _reader.FindGoals(selector.Token, LookupLimit);
        if (matches.Count == 0)
        {
            AddUnavailable(lines, selector, "no goal id matched the cited prefix");
            return;
        }

        if (matches.Count > 1)
        {
            AddUnavailable(lines, selector, "the cited goal prefix is ambiguous");
            return;
        }

        AddGoalEvidence(lines, selector, matches[0], taskFilter: null);
    }

    private void AddTaskLookup(List<string> lines, CitedPriorEvidenceSelector selector)
    {
        var matches = _reader.FindTasks(selector.Token, LookupLimit);
        if (matches.Count == 0)
        {
            AddUnavailable(lines, selector, "no task id matched the cited prefix");
            return;
        }

        if (matches.Count > 1)
        {
            AddUnavailable(lines, selector, "the cited task prefix is ambiguous");
            return;
        }

        AddGoalEvidence(lines, selector, matches[0].Goal, matches[0].Task.Id);
    }

    private void AddUnspecifiedLookup(List<string> lines, CitedPriorEvidenceSelector selector)
    {
        var goals = _reader.FindGoals(selector.Token, LookupLimit);
        var tasks = _reader.FindTasks(selector.Token, LookupLimit);
        if (goals.Count + tasks.Count == 0)
        {
            AddUnavailable(lines, selector, "no goal or task id matched the cited selector");
            return;
        }

        if (goals.Count + tasks.Count > 1)
        {
            AddUnavailable(lines, selector, "the cited selector is ambiguous across goal/task records");
            return;
        }

        if (goals.Count == 1)
        {
            AddGoalEvidence(lines, selector, goals[0], taskFilter: null);
        }
        else
        {
            AddGoalEvidence(lines, selector, tasks[0].Goal, tasks[0].Task.Id);
        }
    }

    private static void AddGoalEvidence(
        List<string> lines,
        CitedPriorEvidenceSelector selector,
        GoalSnapshot goal,
        string? taskFilter)
    {
        var kind = taskFilter is null ? "goal" : "task";
        lines.Add($"## Cited {kind} `{selector.Token}`");
        lines.Add(string.Empty);
        lines.Add($"Resolved goal: `{goal.Id}`.");
        if (taskFilter is not null)
        {
            lines.Add($"Resolved task: `{taskFilter}`.");
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

        lines.Add($"Rounds: {rounds.Length}; showing newest {shown.Length}.");
        if (rounds.Length > shown.Length)
        {
            lines.Add($"> Truncated rounds: {rounds.Length - shown.Length} older rounds omitted.");
        }

        if (shown.Length == 0)
        {
            lines.Add("- No verification or classifier receipt records were available.");
            lines.Add(string.Empty);
            return;
        }

        foreach (var round in shown)
        {
            lines.Add($"- task={round.TaskId}; timestamp={round.Timestamp:O}; {round.Summary}");
            lines.Add($"  classifier_receipt: {round.ClassifierReceipt ?? "unavailable (no stored classifier receipt paired with this verification)"}");
        }

        lines.Add(string.Empty);
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
            .ToArray();
        var receipts = goal.Timeline
            .Where(evt => string.Equals(evt.TaskId, task.Id, StringComparison.OrdinalIgnoreCase))
            .Where(evt => evt.Kind is ProgressKind.TaskNote or ProgressKind.OperatorTaskNote)
            .Where(evt => evt.Message.Contains("CLASSIFIER ", StringComparison.OrdinalIgnoreCase))
            .OrderBy(evt => evt.OccurredAt)
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

    private static void AddUnavailable(List<string> lines, CitedPriorEvidenceSelector selector, string reason)
    {
        lines.Add($"## Cited {selector.Kind.ToString().ToLowerInvariant()} `{selector.Token}`");
        lines.Add(string.Empty);
        lines.Add($"> Records unavailable for cited {selector.Kind.ToString().ToLowerInvariant()} `{selector.Token}`: {Sanitize(reason, MaxReasonChars)}.");
        lines.Add(string.Empty);
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

    private static string BoundUtf8(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxUtf8Bytes)
        {
            return value;
        }

        const string notice = "\n\n> UTF-8 byte cap reached: additional resolved evidence was truncated at 20,000 bytes.";
        var byteBudget = MaxUtf8Bytes - Encoding.UTF8.GetByteCount(notice);
        var chars = value.Length;
        while (chars > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, chars)) > byteBudget)
        {
            chars--;
        }

        if (chars > 0 && char.IsHighSurrogate(value[chars - 1]))
        {
            chars--;
        }

        return value[..chars].TrimEnd() + notice;
    }

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
