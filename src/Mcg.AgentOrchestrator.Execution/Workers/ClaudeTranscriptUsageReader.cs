using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ClaudeTranscriptUsageResult(ProviderReportedUsage? Usage, string UnavailableReason);

public sealed class ClaudeTranscriptUsageReader(
    IReadOnlyList<string> projectsRoots,
    Func<string, Stream>? openRead = null)
{
    public static ClaudeTranscriptUsageReader CreateDefault() => new([
        Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { } configDirectory &&
            !string.IsNullOrWhiteSpace(configDirectory)
                ? configDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            "projects")]);

    public ClaudeTranscriptUsageResult Read(string? sessionId, IReadOnlyList<string>? additionalRoots = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            sessionId.IndexOfAny(['*', '?']) >= 0 ||
            sessionId.Contains("..", StringComparison.Ordinal))
        {
            return new(null, $"claude-transcript-missing: no valid session id was recorded ({sessionId ?? "none"})");
        }

        var fileName = sessionId + ".jsonl";
        string? transcript = null;
        DateTime latest = DateTime.MinValue;
        try
        {
            foreach (var root in projectsRoots.Concat(additionalRoots ?? []))
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var candidate in Directory.EnumerateFiles(root, fileName, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }))
                {
                    var modified = File.GetLastWriteTimeUtc(candidate);
                    if (string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal) &&
                        (transcript is null || modified > latest))
                    {
                        transcript = candidate;
                        latest = modified;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new(null, $"claude-transcript-unreadable: {fileName} search failed ({ex.GetType().Name})");
        }

        if (transcript is null)
            return new(null, $"claude-transcript-missing: no {fileName} under the Claude projects root");

        try
        {
            using var stream = (openRead ?? OpenSharedRead)(transcript);
            using var reader = new StreamReader(stream);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            long input = 0, cached = 0, output = 0;
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var entry = document.RootElement;
                    if (entry.ValueKind != JsonValueKind.Object ||
                        !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                        type.GetString() != "assistant" ||
                        !entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                        !message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                        !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
                        !TryTokens(usage, out var messageInput, out var messageCached, out var messageOutput))
                        continue;

                    var messageId = id.GetString();
                    if (string.IsNullOrWhiteSpace(messageId) || !seenIds.Add(messageId))
                        continue;

                    checked
                    {
                        input += messageInput;
                        cached += messageCached;
                        output += messageOutput;
                    }
                }
                catch (JsonException)
                {
                    // A malformed line does not invalidate provider usage on other lines.
                }
            }

            return seenIds.Count == 0
                ? new(null, $"claude-transcript-no-usage: {fileName} reported no assistant usage")
                : new(new ProviderReportedUsage(input, output, cached), string.Empty);
        }
        catch (Exception ex)
        {
            return new(null, $"claude-transcript-unreadable: {fileName} could not be read ({ex.GetType().Name})");
        }
    }

    private static Stream OpenSharedRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static bool TryTokens(JsonElement usage, out long input, out long cached, out long output)
    {
        input = cached = output = 0;
        return (usage.TryGetProperty("input_tokens", out _) ||
                usage.TryGetProperty("cache_creation_input_tokens", out _) ||
                usage.TryGetProperty("cache_read_input_tokens", out _) ||
                usage.TryGetProperty("output_tokens", out _)) &&
            TryToken(usage, "input_tokens", out var rawInput) &&
            TryToken(usage, "cache_creation_input_tokens", out var created) &&
            TryToken(usage, "cache_read_input_tokens", out cached) &&
            TryToken(usage, "output_tokens", out output) &&
            TryAdd(rawInput, created, cached, out input);
    }

    private static bool TryToken(JsonElement usage, string name, out long value)
    {
        value = 0;
        if (!usage.TryGetProperty(name, out var token) || token.ValueKind == JsonValueKind.Null)
            return true;
        return token.ValueKind == JsonValueKind.Number && token.TryGetInt64(out value) && value >= 0;
    }

    private static bool TryAdd(long first, long second, long third, out long sum)
    {
        sum = 0;
        if (first > long.MaxValue - second || first + second > long.MaxValue - third)
            return false;
        sum = first + second + third;
        return true;
    }
}
