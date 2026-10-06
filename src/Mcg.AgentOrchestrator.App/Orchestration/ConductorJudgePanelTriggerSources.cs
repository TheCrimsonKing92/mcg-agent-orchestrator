using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Producer files are opened for reads only. No producer writer, claim, lease or cursor is advanced.
internal sealed partial class ConductorJudgePanelTriggerSources(
    string eventsDirectory, string conductPath, string authorPath, string cohortPath, string statePath)
{
    internal IReadOnlyList<PanelTrigger> Read()
    {
        var conduct = ReadConduct();
        var timeline = ReadTimeline().ToArray();
        var triggers = timeline.Concat(WithoutApparatusTimelineCopies(timeline, ReadConductDisputes(conduct)))
            .Concat(ReadAuthor(conduct)).Concat(ReadCohort()).ToArray();
        return triggers;
    }

    private static IEnumerable<PanelTrigger> WithoutApparatusTimelineCopies(
        IReadOnlyList<PanelTrigger> timeline, IEnumerable<PanelTrigger> conduct)
    {
        var paired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in conduct)
        {
            if (trigger.Kind == PanelTriggerKind.ApparatusRed && ApparatusRegate(trigger.Text) is { } regate)
            {
                // The policy decision precedes its held GOAL result and owns the packet.
                // Pair once by producer evidence kind and ordinal, never treat unknown SHAs as wildcards.
                var matches = timeline.Where(item => item.Kind == PanelTriggerKind.ApparatusRed &&
                        !paired.Contains(item.TriggerId) && item.RecordedAt <= trigger.RecordedAt &&
                        trigger.GoalId.Length >= 8 && item.GoalId.StartsWith(trigger.GoalId, StringComparison.Ordinal) &&
                        ApparatusRegate(item.Text) is { Ordinal: not null } decision && decision.Kind == regate.Kind &&
                        (regate.Ordinal is null || decision.Ordinal == regate.Ordinal))
                    .OrderByDescending(item => item.RecordedAt).ThenBy(item => item.TriggerId, StringComparer.Ordinal)
                    .ToArray();
                if (matches.Length > 0 && matches.Select(item => item.GoalId).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    paired.Add(matches[0].TriggerId);
                    continue;
                }
            }
            yield return trigger;
        }
    }

    private static (string Kind, string? Ordinal)? ApparatusRegate(string text)
    {
        var normalized = text.Replace('_', ' ');
        var kind = Regex.Match(normalized, @"Acceptance RED classified as apparatus \(([^)]+)\)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!kind.Success) return null;
        var ordinal = Regex.Match(normalized,
            @"(?:restored Verified for re-gate (?<ordinal>\d+/\d+)\.|Re-gating on the next conduct tick \((?<ordinal>\d+/\d+)\))",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        // SanitizeReason can truncate the held reason before the ordinal. The nearest
        // unused decision still owns that copy; do not infer a missing ordinal or SHA.
        return (kind.Groups[1].Value, ordinal.Success ? ordinal.Groups["ordinal"].Value : null);
    }

    internal GoalSnapshot? ReadGoal(string goalId)
    {
        Reads?.GoalRead();
        if (!File.Exists(statePath)) return null;
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(statePath);
        var goal = repository.LoadGoalAsync(new GoalId(goalId)).GetAwaiter().GetResult();
        if (goal is not null || goalId.Length >= 32) return goal;
        // GOAL conduct lines name the short prefix; resolve it using persisted rows only.
        var matches = repository.FindGoalSnapshotsByIdPrefixAsync(goalId, 2).GetAwaiter().GetResult();
        if (matches.Count > 1) throw new InvalidDataException("Ambiguous panel trigger goal prefix: " + goalId);
        return matches.SingleOrDefault();
    }

    private IEnumerable<PanelTrigger> ReadTimeline()
    {
        if (!Directory.Exists(eventsDirectory)) yield break;
        foreach (var path in Directory.EnumerateFiles(eventsDirectory, "*.jsonl").Order(StringComparer.Ordinal))
        foreach (var line in Lines(path))
        {
            if (TimelineTrigger(line) is { } trigger) yield return trigger;
        }
    }

    private static PanelTrigger? TimelineTrigger(string line)
    {
        PanelTrigger? trigger = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var eventType = String(root, "eventType");
            var text = String(root, "reason") ?? String(root, "message") ?? "";
            // Policy notes and operator answers may quote a dispute code. Only the
            // producer's direct decision text is itself a new dispute.
            var decisionDispute = text.StartsWith("PRE_REVIEW_RED_UNCHANGED_CANDIDATE:", StringComparison.Ordinal) ||
                text.StartsWith("PRE_REVIEW_EVIDENCE_TIMEOUT:", StringComparison.Ordinal) ||
                text.StartsWith("PRE_TESTER_RED_LOOP:", StringComparison.Ordinal) ||
                text.StartsWith("Acceptance RED classified as apparatus", StringComparison.Ordinal);
            if ((eventType == "GoalEscalated" ||
                 String(root, "progressKind") == "GoalPolicyDecision" && decisionDispute) &&
                String(root, "source") != "author-owner-question")
            {
                var goal = String(root, "goalId");
                if (goal is not null && root.TryGetProperty("cursor", out var cursor) &&
                    root.TryGetProperty("timestamp", out var time) && time.TryGetDateTimeOffset(out var at))
                    trigger = Dispute(goal, $"goal-event:{goal}:{cursor}", at, text);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        return trigger;
    }

    private IReadOnlyList<OwnerConductEvent> ReadConduct()
    {
        var directory = Path.GetDirectoryName(conductPath);
        if (directory is null || !Directory.Exists(directory)) return [];
        var result = new List<OwnerConductEvent>();
        var files = Directory.EnumerateFiles(directory, Path.GetFileNameWithoutExtension(conductPath) + "-*.log")
            .Prepend(conductPath).Distinct(StringComparer.Ordinal);
        foreach (var file in files)
        foreach (var line in Lines(file))
            if (ConductEventFileSource.TryParse(line, out var item)) result.Add(item!);
        return result.OrderBy(item => item.Timestamp).ToArray();
    }

    private static IEnumerable<PanelTrigger> ReadConductDisputes(IEnumerable<OwnerConductEvent> events)
    {
        foreach (var item in events.Where(item => item.GoalId is not null &&
                     item.EventKind is "goal-escalation" or "goal" &&
                     !item.Detail.Contains("author-owner-question", StringComparison.Ordinal)))
        {
            var id = $"conduct-event:{item.GoalId}:{item.Timestamp:O}";
            if (Dispute(item.GoalId!, id, item.Timestamp, item.Detail) is { } trigger) yield return trigger;
        }
    }

    private static PanelTrigger? Dispute(string goal, string id, DateTimeOffset at, string text)
    {
        (PanelTriggerKind? kind, string detail) = text switch
        {
            var s when s.Contains("verification-inconclusive-unchanged-inputs", StringComparison.Ordinal) ||
                       s.Contains("stayed verification-inconclusive on unchanged inputs", StringComparison.Ordinal)
                => (PanelTriggerKind.TesterInconclusiveUnchanged, "verification-inconclusive-unchanged-inputs"),
            var s when s.Contains("PRE_REVIEW_RED_UNCHANGED_CANDIDATE:", StringComparison.Ordinal)
                => (PanelTriggerKind.PreReviewEvidence, "PRE_REVIEW_RED_UNCHANGED_CANDIDATE"),
            var s when s.Contains("PRE_REVIEW_EVIDENCE_TIMEOUT:", StringComparison.Ordinal)
                => (PanelTriggerKind.PreReviewEvidence, "PRE_REVIEW_EVIDENCE_TIMEOUT"),
            var s when s.Contains("PRE_TESTER_RED_LOOP:", StringComparison.Ordinal)
                => (PanelTriggerKind.PreTesterRedLoop, "PRE_TESTER_RED_LOOP"),
            var s when s.Contains("Acceptance RED classified as apparatus", StringComparison.Ordinal) ||
                       s.Contains("Acceptance_RED_classified_as_apparatus", StringComparison.Ordinal)
                => (PanelTriggerKind.ApparatusRed, "apparatus"),
            _ => ((PanelTriggerKind?)null, "")
        };
        return kind is null ? null : new(kind.Value, goal,
            Revision(text, @"(?:candidate_sha=|branch=|candidate[\s_]+)"),
            Revision(text, @"(?:base_sha=|base=|main=)"), id, at, text, [], detail);
    }

    private static string Revision(string text, string prefix)
    {
        var match = Regex.Match(text, prefix + @"([a-fA-F0-9]{12,40})(?![a-fA-F0-9])",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : PanelTrigger.UnrecordedSha;
    }

    private static string? String(JsonElement root, string key) => root.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private IEnumerable<string> Lines(string path)
    {
        if (!File.Exists(path)) yield break;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            Reads?.EventLine();
            // ReadLine also returns a torn final record; never act on it even if it parses.
            if (reader.EndOfStream && stream.Length > 0)
            {
                var position = stream.Position;
                stream.Position = stream.Length - 1;
                var terminated = stream.ReadByte() == '\n';
                stream.Position = position;
                if (!terminated) yield break;
            }
            Reads?.ParsedLine();
            yield return line;
        }
    }
}
