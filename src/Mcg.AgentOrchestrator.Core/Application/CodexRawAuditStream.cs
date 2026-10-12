using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

// Reads the raw provider records retained beside normalized worker stdout; owns no cached state.
internal static class CodexRawAuditStream
{
    internal static IEnumerable<string> ReadLines(string? standardOutputPath)
    {
        if (string.IsNullOrWhiteSpace(standardOutputPath))
        {
            yield break;
        }

        var path = standardOutputPath + ".jsonl";
        if (!File.Exists(path))
        {
            yield break;
        }

        StreamReader reader;
        try
        {
            reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (reader)
        {
            while (true)
            {
                string? line;
                try
                {
                    line = reader.ReadLine();
                }
                catch (IOException)
                {
                    yield break;
                }
                catch (UnauthorizedAccessException)
                {
                    yield break;
                }

                if (line is null)
                {
                    yield break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line.TrimEnd();
                }
            }
        }
    }

    internal static bool TryGetFailedTurnMessage(string? standardOutputPath, out string message)
    {
        message = string.Empty;
        foreach (var line in ReadLines(standardOutputPath))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    (!root.TryGetProperty("type", out var discriminator) &&
                     !root.TryGetProperty("kind", out discriminator)) ||
                    discriminator.ValueKind != JsonValueKind.String ||
                    discriminator.GetString() is not ("error" or "turn.failed"))
                {
                    continue;
                }

                // The last failed record owns the display cause, even when it has no message.
                message = string.Empty;
                var candidate = ReadMessage(root);
                if (string.IsNullOrWhiteSpace(candidate) &&
                    root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    candidate = ReadMessage(error);
                }

                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    var sanitized = Regex.Replace(candidate, @"\p{Cc}+", " ").Trim();
                    message = sanitized.Length > 200 ? sanitized[..200] : sanitized;
                }
            }
            catch (JsonException)
            {
                // Malformed audit lines carry no usable provider record.
            }
        }

        return message.Length > 0;
    }

    private static string? ReadMessage(JsonElement record) =>
        record.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
