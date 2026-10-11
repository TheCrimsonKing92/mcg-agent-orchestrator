using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Versioned owner input, written atomically beside the discovery snapshot.</summary>
public static class OwnerAnswerFile
{
    public const string FileName = "owner-answers.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static IReadOnlyList<OwnerAnswer> Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            var document = JsonSerializer.Deserialize<AnswerDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.SchemaVersion != 1 || document.Answers is null)
                throw new InvalidDataException($"{FileName} requires schemaVersion 1 and an answers array.");

            var answers = new List<OwnerAnswer>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Answers)
            {
                if (entry is null)
                    throw new InvalidDataException($"{FileName} answers must contain objects.");
                var answer = new OwnerAnswer(entry.FactKey!, entry.Value!, entry.Reason, entry.RecordedUtc);
                if (!keys.Add(answer.FactKey))
                    throw new InvalidDataException($"{FileName} has duplicate key '{answer.FactKey}'.");
                answers.Add(answer);
            }
            return answers.AsReadOnly();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Array.Empty<OwnerAnswer>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"{FileName} could not be read: {exception.Message}", exception);
        }
    }

    public static void Write(string directory, IReadOnlyList<OwnerAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var entries = answers.GroupBy(answer => answer.FactKey, StringComparer.Ordinal)
            .Select(group => group.Last()).OrderBy(answer => answer.FactKey, StringComparer.Ordinal)
            .Select(answer => new AnswerEntry
            {
                FactKey = answer.FactKey, Value = answer.Value, Reason = answer.Reason, RecordedUtc = answer.RecordedUtc
            }).ToArray();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".owner-answers-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new AnswerDocument
            {
                SchemaVersion = 1, Answers = entries
            }, JsonOptions));
            File.Move(temporaryPath, Path.Combine(directory, FileName), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed class AnswerDocument
    {
        [JsonRequired] public int SchemaVersion { get; init; }
        [JsonRequired] public AnswerEntry?[]? Answers { get; init; }
    }

    private sealed class AnswerEntry
    {
        [JsonRequired] public string? FactKey { get; init; }
        [JsonRequired] public string? Value { get; init; }
        public string? Reason { get; init; }
        [JsonRequired] public DateTimeOffset RecordedUtc { get; init; }
    }
}
