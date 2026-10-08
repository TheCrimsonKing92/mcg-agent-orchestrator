using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleSession(
    IOrchestratorStateQueries state,
    IOwnerQuestionSource questions,
    IOwnerAnswerSubmitter answers,
    IConductorLiveness liveness,
    IOwnerDigestSummary digest,
    IGoalEventTail eventTail,
    IOwnerConsoleOutput output,
    TimeProvider clock,
    IOwnerConsoleConductor? conductor = null,
    IOwnerConsoleDigestReport? digestReport = null,
    ChangeStreamFileReader? changes = null)
{
    private readonly OwnerConsoleControlCommands _control = new(conductor, digestReport, output, clock);
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, OwnerQuestion> _open = [];
    private readonly HashSet<int> _retired = [];
    private int _hiddenCount;
    private int _nextNumber = 1;
    private bool _bell = true;
    private DateTimeOffset? _lastConductEvent;
    private long _coveredSequence;
    private readonly Dictionary<string, OwnerGoalCard> _board = new(StringComparer.OrdinalIgnoreCase);

    internal async Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken)
    {
        _lastConductEvent = lastActivity;
        _coveredSequence = changes?.Reanchor() ?? 0;
        foreach (var line in digest.ReadSummaryLines().Take(5))
            Announce(line);
        var metadata = await RefreshQuestionsAsync(cancellationToken);
        await PrintHeaderAsync(metadata, cancellationToken);
        await PrintBoardAsync(metadata, cancellationToken);
        Announce("Type help for commands.");
    }

    internal async Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken)
    {
        _lastConductEvent = item.Timestamp;
        if (changes is not null)
        {
            await ApplyChangesAsync(cancellationToken);
            return;
        }
        var metadata = await RefreshQuestionsAsync(cancellationToken);
        if (item.EventKind is "watch-transition" or "acceptance" or "loop-relaunch" or "goal-escalation")
            await PrintBoardAsync(metadata, cancellationToken);
    }

    private async Task ApplyChangesAsync(CancellationToken cancellationToken)
    {
        var records = changes!.ReadAvailable(out var discontinuity);
        if (discontinuity is not null)
        {
            await RefreshAllAsync(cancellationToken);
            return;
        }
        foreach (var record in records)
        {
            if (record.Sequence <= _coveredSequence) continue;
            // The reader consumes the batch before state reads complete. A failed read can
            // leave session coverage behind even when the reader sees contiguous sequences.
            if (record.Sequence > _coveredSequence + 1)
            {
                await RefreshAllAsync(cancellationToken);
                return;
            }
            if (record.ChangeKind == ChangeStreamRecord.OwnerDecisionRaised)
                await RefreshQuestionsAsync(cancellationToken);
            else
            {
                var id = record.GoalId;
                if (id.Length != 32)
                {
                    var matches = _board.Keys.Where(key => key.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1)
                    {
                        await RefreshAllAsync(cancellationToken);
                        return;
                    }
                    id = matches[0];
                }
                var kernel = await state.LoadGoalsAsync([new GoalId(id)], cancellationToken);
                var goal = kernel.Goals.SingleOrDefault(goal => goal.Id.Value.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (goal is null || !IsActive(goal.Status))
                {
                    _board.Remove(id);
                    Announce($"{id[..Math.Min(8, id.Length)]} | removed from board");
                }
                else
                {
                    var card = ToCard(goal);
                    _board[id] = card;
                    PrintBoardRow(card);
                }
            }
            _coveredSequence = record.Sequence;
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var anchor = changes!.Reanchor();
        var metadata = await RefreshQuestionsAsync(cancellationToken);
        await PrintBoardAsync(metadata, cancellationToken);
        _coveredSequence = anchor;
    }

    internal async Task<bool> HandleCommandAsync(string raw, CancellationToken cancellationToken)
    {
        var metadata = await RefreshQuestionsAsync(cancellationToken);
        var line = raw.TrimEnd('\r').Trim();
        if (line.Length == 0) return true;
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        switch (command)
        {
            case "quit": return false;
            case "help":
                Announce("board | goal <id-prefix> | answer <n> <text> | accept <n> | bell on|off | help | quit");
                output.WriteLine("conductor start [--clear-stop] | conductor stop [--yes] | conductor status");
                output.WriteLine("digest | metrics | board");
                break;
            case "board": await PrintBoardAsync(metadata, cancellationToken); break;
            case "conductor": _control.HandleConductor(line); break;
            case "digest":
                if (parts.Length != 1) Announce("usage: digest");
                else await PrintDigestAsync(metadata, cancellationToken);
                break;
            case "metrics": _control.HandleMetrics(line); break;
            case "goal":
                if (parts.Length < 2) Announce("usage: goal <id-prefix>");
                else await PrintGoalAsync(parts[1], cancellationToken);
                break;
            case "bell":
                if (parts.Length < 2 || parts[1] is not ("on" or "off")) Announce("usage: bell on|off");
                else { _bell = parts[1] == "on"; Announce($"bell {parts[1]}"); }
                break;
            case "answer":
            case "accept":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var number) || number < 1)
                { Announce($"usage: {command} <n>{(command == "answer" ? " <text>" : "")}"); break; }
                await SubmitAsync(command, number, parts.Length == 3 ? parts[2] : null, cancellationToken);
                break;
            default: Announce("unknown command; type help"); break;
        }
        return true;
    }

    private async Task SubmitAsync(string command, int number, string? text, CancellationToken cancellationToken)
    {
        if (!_open.TryGetValue(number, out var question))
        {
            Announce(_retired.Contains(number) ? $"question {number} is no longer open" : $"no question {number}");
            return;
        }
        if (question.Kind == OwnerQuestionKind.StewardHold)
        { Announce($"Steward questions are answered through goal verbs for now; use the CLI retry/adjudicate commands for goal {question.GoalId}."); return; }
        if (command == "accept")
        {
            text = question.ProposedDefault;
            if (string.IsNullOrWhiteSpace(text)) { Announce($"question {number} has no proposed default"); return; }
        }
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith("--", StringComparison.Ordinal))
        { Announce("usage: answer <n> <text> (text cannot start with --)"); return; }
        try
        {
            answers.Submit(question, text);
            await RefreshQuestionsAsync(cancellationToken);
            Announce(_open.ContainsKey(number)
                ? $"question {number} is still open; answer was not accepted"
                : $"answered question {number}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        { Announce($"error: {ex.Message}"); await RefreshQuestionsAsync(cancellationToken); }
    }

    private async Task<IReadOnlyList<GoalSummary>?> RefreshQuestionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GoalSummary>? metadata = null;
        OwnerQuestionSnapshot snapshot;
        if (questions is OwnerQuestionReadModel model)
        {
            metadata = await state.ListGoalMetadataAsync(cancellationToken);
            snapshot = await model.ReadAsync(metadata, cancellationToken);
        }
        else
            snapshot = await questions.ReadAsync(cancellationToken);
        var current = snapshot.Live;
        _hiddenCount = snapshot.Hidden.Count;
        var ids = current.Select(item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _open.Where(pair => !ids.Contains(pair.Value.ItemId)).ToArray())
        { _open.Remove(pair.Key); _retired.Add(pair.Key); }
        foreach (var question in current)
        {
            if (!_numbers.TryGetValue(question.ItemId, out var number))
            { number = _nextNumber++; _numbers.Add(question.ItemId, number); PrintQuestion(number, question); }
            if (!_retired.Contains(number)) _open[number] = question;
        }
        return metadata;
    }

    private void PrintQuestion(int number, OwnerQuestion question)
    {
        if (_bell) output.Write("\a");
        var fields = new[]
        {
            question.BlastRadius is null ? null : $"blast radius: {question.BlastRadius}",
            question.Confidence is null ? null : $"confidence: {question.Confidence}",
            question.ProposedDefault is null ? null : $"default: {question.ProposedDefault}"
        }.Where(field => field is not null);
        var stewardGuidance = question.Kind == OwnerQuestionKind.StewardHold
            ? $" | view only | CLI: mcg-orchestrator.cmd retry --goal {question.GoalId} <task-number> <message> or mcg-orchestrator.cmd adjudicate --goal {question.GoalId} <task-number> <close|reopen-regate|route> --text-file <path> --evidence <ref>"
            : string.Empty;
        Announce($"[{number}] {question.GoalId[..Math.Min(8, question.GoalId.Length)]} {question.Text}{(fields.Any() ? " | " + string.Join(" | ", fields) : "")}{stewardGuidance}");
    }

    private async Task PrintHeaderAsync(IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        var goals = await LoadActiveAsync(metadata, cancellationToken);
        var age = _lastConductEvent is null ? "unknown" : Age(_lastConductEvent.Value);
        var hidden = _hiddenCount > 0 ? $" | hidden: {_hiddenCount}" : string.Empty;
        Announce($"conductor: {(liveness.IsRunning() ? "running" : "stopped")} | active goals: {goals.Count} | owner questions: {_open.Count}{hidden} | last event: {age}");
    }

    private async Task PrintBoardAsync(IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        var goals = await LoadActiveAsync(metadata, cancellationToken);
        _board.Clear();
        foreach (var goal in goals) _board[goal.Id] = goal;
        Announce($"board | active goals: {goals.Count} | owner questions: {_open.Count}");
        foreach (var goal in goals)
            PrintBoardRow(goal);
    }

    private void PrintBoardRow(OwnerGoalCard goal) =>
        output.WriteLine($"{goal.Id[..Math.Min(8, goal.Id.Length)]} | {goal.Title.Replace('\r', ' ').Replace('\n', ' ')} | {goal.State} | {goal.CurrentRole?.ToString() ?? "-"} | {Age(goal.LastEvent)}");

    private async Task PrintGoalAsync(string prefix, CancellationToken cancellationToken)
    {
        var metadata = await state.ListGoalMetadataAsync(cancellationToken);
        var matches = metadata.Where(item => item.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) { Announce($"no goal matches '{prefix}'"); return; }
        if (matches.Length > 1) { Announce($"ambiguous goal: {string.Join(", ", matches.Select(item => item.Id[..8]))}"); return; }
        var kernel = await state.LoadGoalsAsync([new GoalId(matches[0].Id)], cancellationToken);
        var goal = kernel.Goals.SingleOrDefault();
        if (goal is null) { Announce("goal state unavailable"); return; }
        Announce($"{goal.Id.Value} | {OwnerGoalTitle.From(goal.Objective)} | {goal.Status}");
        foreach (var line in eventTail.ReadLast(goal.Id.Value, 15)) output.WriteLine(line);
    }

    private async Task<IReadOnlyList<OwnerGoalCard>> LoadActiveAsync(
        IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        metadata ??= await state.ListGoalMetadataAsync(cancellationToken);
        var ids = metadata.Where(item => Enum.TryParse<GoalStatus>(item.Status, true, out var status) && IsActive(status))
            .Select(item => new GoalId(item.Id)).ToArray();
        if (ids.Length == 0) return [];
        var kernel = await state.LoadGoalsAsync(ids, cancellationToken);
        return kernel.Goals.Where(goal => IsActive(goal.Status)).Select(ToCard).ToArray();
    }

    private static OwnerGoalCard ToCard(Goal goal)
    {
        var task = goal.Tasks.FirstOrDefault(item => item.Status != WorkTaskStatus.Completed) ?? goal.Tasks.LastOrDefault();
        return new OwnerGoalCard(goal.Id.Value, OwnerGoalTitle.From(goal.Objective), goal.Status, task?.RequiredRole,
            goal.Timeline.OrderByDescending(item => item.OccurredAt).FirstOrDefault()?.OccurredAt);
    }

    private static bool IsActive(GoalStatus status) => status is not
        (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded);

    private void Announce(string line) => output.WriteLine(ConsoleAnnouncementFormatter.Format(clock, line));

    private async Task PrintDigestAsync(IReadOnlyList<GoalSummary>? metadata, CancellationToken cancellationToken)
    {
        foreach (var line in digest.ReadSummaryLines().Take(5)) Announce(line);
        await PrintHeaderAsync(metadata, cancellationToken);
        await PrintBoardAsync(metadata, cancellationToken);
    }

    private string Age(DateTimeOffset? timestamp)
    {
        if (timestamp is null) return "unknown";
        var span = clock.GetUtcNow() - timestamp.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? $"{span.TotalSeconds:0}s" : span.TotalHours < 1 ? $"{span.TotalMinutes:0}m" : $"{span.TotalHours:0}h";
    }
}
