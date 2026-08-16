using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record CodexJsonlParseResult(
    string WorkerOutput,
    ProviderReportedUsage? Usage,
    string UsageUnavailableReason,
    bool Recognized);

public static class CodexJsonlUsageParser
{
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
            workerOutputRecognized || usageContractRecognized);
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
