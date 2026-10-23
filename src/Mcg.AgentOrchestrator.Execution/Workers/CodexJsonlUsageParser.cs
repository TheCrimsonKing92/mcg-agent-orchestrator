using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record CodexJsonlParseResult(
    string WorkerOutput,
    ProviderReportedUsage? Usage,
    string UsageUnavailableReason,
    bool Recognized,
    IReadOnlyList<string>? AgentMessages = null,
    int MalformedLineCount = 0);

public static class CodexJsonlUsageParser
{
    // Internal completions require a successful final turn. Keep this strict contract separate
    // from Parse, whose tolerant, concatenated worker output and usage behavior is intentional.
    public static bool TryParseCompletedAnswer(string jsonl, out string answer, out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(jsonl);
        answer = string.Empty;
        failureReason = string.Empty;
        var openTurn = false;
        var completedTurn = false;
        string? candidate = null;
        using var reader = new StringReader(jsonl);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = GetString(root, "type");
                if (string.IsNullOrWhiteSpace(type))
                    return Fail("codex-transport:malformed-event", out failureReason);

                switch (type)
                {
                    case "turn.started":
                        if (openTurn) return Fail("codex-transport:nested-turn", out failureReason);
                        openTurn = true;
                        completedTurn = false;
                        candidate = null;
                        break;
                    case "turn.completed":
                        if (!openTurn) return Fail("codex-transport:unmatched-turn-completion", out failureReason);
                        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Object)
                            return Fail("codex-transport:malformed-event", out failureReason);
                        openTurn = false;
                        completedTurn = true;
                        break;
                    case "turn.failed":
                    case "error":
                        return Fail("codex-transport:failed-turn", out failureReason);
                    case "item.started":
                    case "item.updated":
                    case "item.completed":
                        if (!openTurn) return Fail("codex-transport:item-outside-turn", out failureReason);
                        if (!root.TryGetProperty("item", out var item) || string.IsNullOrWhiteSpace(GetString(item, "type")))
                            return Fail("codex-transport:malformed-event", out failureReason);
                        if (type == "item.completed" && GetString(item, "type") == "agent_message")
                        {
                            candidate = GetString(item, "text");
                            if (candidate is null) return Fail("codex-transport:malformed-event", out failureReason);
                        }
                        break;
                    // Thread/usage and future informational events cannot supply an answer.
                }
            }
            catch (JsonException)
            {
                return Fail("codex-transport:malformed-json", out failureReason);
            }
        }

        if (openTurn || !completedTurn)
            return Fail("codex-transport:incomplete-turn", out failureReason);
        if (string.IsNullOrWhiteSpace(candidate))
            return Fail("codex-transport:missing-final-assistant", out failureReason);
        answer = candidate;
        return true;
    }

    private static bool Fail(string reason, out string failureReason)
    {
        // Fixed protocol classifications, never provider-supplied prose or JSON fragments.
        failureReason = reason;
        return false;
    }

    public static CodexJsonlParseResult Parse(string jsonl)
    {
        ArgumentNullException.ThrowIfNull(jsonl);
        var messages = new List<string>();
        long? input = null;
        long? cached = null;
        long? output = null;
        var workerOutputRecognized = false;
        var usageContractRecognized = false;
        var malformedUsage = false;
        var malformedLineCount = 0;

        using var reader = new StringReader(jsonl);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var type = GetString(root, "type");
                if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                    GetString(item, "type") == "agent_message" && GetString(item, "text") is { } text)
                {
                    messages.Add(text);
                    workerOutputRecognized = true;
                }

                if (type == "turn.completed")
                {
                    usageContractRecognized = true;
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        if (usage.ValueKind != JsonValueKind.Object)
                        {
                            malformedUsage = true;
                        }
                        else
                        {
                            malformedUsage |= !TryReadOptionalInt(usage, "input_tokens", ref input);
                            malformedUsage |= !TryReadOptionalInt(usage, "cached_input_tokens", ref cached);
                            malformedUsage |= !TryReadOptionalInt(usage, "output_tokens", ref output);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Raw JSONL stays in the process log. Unknown lines are not reinterpreted as telemetry.
                malformedLineCount++;
            }
        }

        var usageResult = input is not null || cached is not null || output is not null
            ? new ProviderReportedUsage(input, output, cached)
            : null;
        var reason = malformedUsage ? "malformed" : usageContractRecognized ? "absent" : "unsupported";
        return new CodexJsonlParseResult(
            string.Join(Environment.NewLine, messages),
            usageResult,
            reason,
            workerOutputRecognized || usageContractRecognized,
            messages.ToArray(),
            malformedLineCount);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadOptionalInt(JsonElement usage, string propertyName, ref long? target)
    {
        if (!usage.TryGetProperty(propertyName, out var value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var reported) || reported < 0)
        {
            return false;
        }

        target = reported;
        return true;
    }
}
