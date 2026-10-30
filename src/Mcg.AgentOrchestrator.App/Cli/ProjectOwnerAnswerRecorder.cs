using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// The complete validation-and-write boundary for project owner answers.
internal static class ProjectOwnerAnswerRecorder
{
    internal static ProjectOwnerAnswerOutcome Record(string directory, string key, string value,
        string? reason, DateTimeOffset now)
    {
        var (model, failure) = ReadModel(directory);
        if (failure is not null) return failure;
        try { value = OwnerAnswerValidator.Validate(key, value); }
        catch (ArgumentException exception) { return new(false, exception.Message); }

        var utc = now.ToUniversalTime();
        utc = new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var answer = new OwnerAnswer(key, value, reason, utc);
        var applied = OwnerAnswerApplier.Apply(model!, [answer]);
        if (applied.AnsweredFacts.Count == 0)
            return new(false, $"{key}: {applied.StaleAnswers[0].Reason}");
        var (answers, error) = ReadAnswers(directory);
        if (error is not null) return new(false, error, ErrorPrefix: "Owner answers unreadable: ");
        OwnerAnswerFile.Write(directory, [.. answers, answer]);
        return new(true, "Owner answer recorded", answer);
    }

    internal static ProjectOwnerAnswerOutcome ReplaceStale(string directory, string key, string value,
        string? reason, DateTimeOffset now)
    {
        var (model, failure) = ReadModel(directory);
        if (failure is not null) return failure;
        if (!OwnerAnswerApplier.IsLiveKey(model!, key))
            return new(false, $"{key}: {OwnerAnswerApplier.NotLiveReason}");
        return Record(directory, key, value, reason, now);
    }

    internal static ProjectOwnerAnswerOutcome Remove(string directory, string key)
    {
        var (model, failure) = ReadModel(directory);
        if (failure is not null) return failure;
        var (answers, error) = ReadAnswers(directory);
        if (error is not null) return new(false, error, ErrorPrefix: "Owner answers unreadable: ");
        var applied = OwnerAnswerApplier.Apply(model!, answers);
        // A stale value must not authorize deleting an accepted answer for the same key.
        if (applied.AnsweredFacts.Any(fact => fact.FactKey == key))
            return new(false, $"{key}: a live answer cannot be dismissed");
        if (!applied.StaleAnswers.Any(stale => stale.Answer.FactKey == key))
            return new(false, $"{key}: no stale answer to dismiss");
        OwnerAnswerFile.Write(directory, answers.Where(answer => answer.FactKey != key).ToArray());
        return new(true, "Stale answer dismissed");
    }

    internal static (IReadOnlyList<OwnerAnswer> Answers, string? Error) ReadAnswers(string directory)
    {
        try { return (OwnerAnswerFile.Read(directory), null); }
        catch (InvalidDataException exception) { return (Array.Empty<OwnerAnswer>(), exception.Message); }
    }

    private static (ProjectModel? Model, ProjectOwnerAnswerOutcome? Failure) ReadModel(string directory)
    {
        var path = Path.Combine(directory, "project-model.json");
        try { return (ProjectModelJson.Deserialize(File.ReadAllText(path)), null); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return (null, new(false, $"Missing project model {path}; run project discover.")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidOperationException or InvalidDataException)
        { return (null, new(false, $"{exception.Message}; run project discover.", ErrorPrefix: "Project model unreadable: ")); }
    }
}
