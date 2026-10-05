using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerSkillReadSet(IReadOnlyList<string> Skills, int MalformedLineCount);

/// <summary>Records targeted skill paths, not whether their content was applied.</summary>
public static class WorkerSkillReadParser
{
    private static readonly Regex SkillPath = new(
        @"\.agents[\\/]+skills[\\/]+([a-z0-9][a-z0-9_-]*)[\\/]+SKILL\.md\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static WorkerSkillReadSet ParseCodexEvents(string text) => Parse(text, claude: false);
    public static WorkerSkillReadSet ParseClaudeTranscript(string text) => Parse(text, claude: true);

    private static WorkerSkillReadSet Parse(string text, bool claude)
    {
        var skills = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var malformed = 0;
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!claude)
                {
                    if (String(root, "type") == "item.started" && Object(root, "item", out var item) &&
                        String(item, "type") == "command_execution") Add(String(item, "command"));
                }
                else if (String(root, "type") == "assistant" && Object(root, "message", out var message) &&
                    message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (String(block, "type") != "tool_use" || !Object(block, "input", out var input)) continue;
                        Add(String(block, "name") switch
                        {
                            "Read" => String(input, "file_path"),
                            "Bash" => String(input, "command"),
                            _ => null
                        });
                    }
                }
            }
            catch (JsonException) { malformed++; }
        }
        return new(skills.ToArray(), malformed);

        void Add(string? target)
        {
            if (target is null) return;
            foreach (Match match in SkillPath.Matches(target))
            {
                var name = match.Groups[1].Value.ToLowerInvariant();
                if (seen.Add(name)) skills.Add(name);
            }
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Object(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object;
    }
}
