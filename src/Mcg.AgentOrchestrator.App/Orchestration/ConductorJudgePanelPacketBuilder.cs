using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PanelPacketTruncation(string Section, int CharactersDropped);
internal sealed record PanelPacket(string Text, IReadOnlyList<PanelPacketTruncation> Truncations);

internal sealed partial class ConductorJudgePanelPacketBuilder(
    string repositoryRoot, Func<string, string, IReadOnlyList<string>, string>? diffProvider = null)
{
    internal const int MaxPacketCharacters = 12_000;
    internal const string AuthorityBoundary =
        "Workers may not edit tests to make them pass, change acceptance criteria or close tasks.\n" +
        "The operator may close or route tasks, rule on frozen-fact amendments, revert a landing on main and answer owner questions.\n" +
        "The judge only recommends.";
    private const string TruncatedMarker = "[truncated]";

    internal PanelPacket Build(PanelTrigger trigger, PanelCriteriaSnapshot criteria)
    {
        var protectedText = $"Criteria (version {criteria.Version})\ncriteria_provenance = \"{criteria.Provenance}\"\n" +
                            string.Join("\n", criteria.Criteria) +
                            "\n\nAuthority boundary\n" + AuthorityBoundary + "\n\n";
        if (protectedText.Length + TruncatedMarker.Length + 2 > MaxPacketCharacters)
            throw new InvalidDataException("Panel protected criteria and authority sections exceed MaxPacketCharacters.");
        var evidence = ReadEvidence(trigger);
        var sections = new List<Section>
            { new("Escalation/question", trigger.Text, 3), new("Trigger detail", trigger.Detail, 3) };
        foreach (var failure in evidence.Failures)
        {
            sections.Add(new("Failing test", failure.TestName ?? "unrecorded", 2));
            sections.Add(new("TRX Message", failure.Message ?? "", 2));
            sections.Add(new("First stack frame", FirstFrame(failure.StackTrace), 1));
        }
        foreach (var note in evidence.Notes) sections.Add(new("Receipt", note, 2));
        var paths = ExplicitPaths(trigger.Text + "\n" + string.Join("\n", evidence.Failures.Select(f => FirstFrame(f.StackTrace))));
        var diff = Diff(trigger, paths);
        foreach (var hunk in SplitHunks(diff)) sections.Add(new("Diff hunk", hunk, 0));

        var truncations = new List<PanelPacketTruncation>();
        void RecordDrop(string title, int count)
        {
            var index = truncations.FindIndex(item => item.Section == title);
            if (index < 0) truncations.Add(new(title, count));
            else truncations[index] = truncations[index] with
                { CharactersDropped = truncations[index].CharactersDropped + count };
        }
        string Render() => protectedText + string.Join("\n\n", sections.Select(section => section.Title + "\n" + section.Text)) +
            (truncations.Count == 0 ? "" : "\n\nTruncation\n" + JsonSerializer.Serialize(truncations));
        // Deterministic order: largest diff hunk first, then frames, messages, then escalation.
        foreach (var section in sections.OrderBy(section => section.Priority).ThenByDescending(section => section.Text.Length))
        {
            var excess = Render().Length - MaxPacketCharacters;
            if (excess <= 0) break;
            if (section.Text.Length == 0) continue;
            var originalLength = section.Text.Length;
            var previousDrop = truncations.FirstOrDefault(item => item.Section == section.Title)?.CharactersDropped ?? 0;
            // Reserve the metadata for this cut before computing available evidence space.
            // Its digit count can only shrink when the actual retained prefix is known.
            RecordDrop(section.Title, originalLength);
            excess = Render().Length - MaxPacketCharacters;
            var retained = Math.Max(0, originalLength - excess - TruncatedMarker.Length);
            section.Text = section.Text[..retained] + TruncatedMarker;
            section.RemainingCharacters = retained;
            var index = truncations.FindIndex(item => item.Section == section.Title);
            truncations[index] = new(section.Title, previousDrop + originalLength - retained);
        }
        // A very large count of failures can itself exhaust the budget through section headings.
        // Keep protected text and one counted marker; record every removed evidence section.
        while (Render().Length > MaxPacketCharacters && sections.Count > 0)
        {
            var section = sections[^1];
            sections.RemoveAt(sections.Count - 1);
            RecordDrop(section.Title, section.RemainingCharacters);
        }
        if (truncations.Count > 0 && !sections.Any(section => section.Text.Contains(TruncatedMarker, StringComparison.Ordinal)))
        {
            while (Render().Length + TruncatedMarker.Length + 2 > MaxPacketCharacters && sections.Count > 0)
            {
                var removed = sections[^1];
                sections.RemoveAt(sections.Count - 1);
                RecordDrop(removed.Title, removed.RemainingCharacters);
            }
            protectedText += TruncatedMarker + "\n\n";
        }
        if (Render().Length > MaxPacketCharacters)
            throw new InvalidDataException("Panel protected criteria and authority sections exceed MaxPacketCharacters with truncation metadata.");
        return new(Render(), truncations);
    }

    private sealed class Section(string title, string text, int priority)
    {
        internal string Title { get; } = title;
        internal string Text { get; set; } = text;
        internal int RemainingCharacters { get; set; } = text.Length;
        internal int Priority { get; } = priority;
    }
}
