using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectAnswerCommand
{
    internal const string Usage = "Usage: project answer [name] <fact-key> <value> [--reason <text>] (delivery: direct or pull-request; delivery-decider: <login>)";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output,
        Func<DateTimeOffset>? clock = null)
    {
        var (name, key, value, reason) = ParseOptions(parts);
        OrchestratorProject project;
        try
        {
            name = name is null ? null : OrchestratorProjectSelection.NormalizeProjectName(name);
            project = name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride)
                : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                    ? new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                    : registry.GetRequiredProject(name);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{Usage}{Environment.NewLine}{exception.Message}", exception);
        }

        if (key is ProjectDelivery.ModeKey or ProjectDelivery.DeciderKey)
            return ProjectDeliveryAnswerCommand.Execute(registry, project, key, value, reason, output, clock);

        var directory = ProjectDiscoveryModelLocation.ResolveDirectory(project, null, defaultRootDirectory,
            registry.DataRootDirectory);
        var modelPath = Path.Combine(directory, "project-model.json");
        if (!File.Exists(modelPath))
        {
            output.WriteLine($"Refused: Missing project model {modelPath}; run project discover.");
            return 1;
        }

        ProjectModel model;
        try
        {
            model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidOperationException or InvalidDataException)
        {
            output.WriteLine($"Project model unreadable: {exception.Message}; run project discover.");
            return 1;
        }

        try
        {
            value = OwnerAnswerValidator.Validate(key, value);
        }
        catch (ArgumentException exception)
        {
            output.WriteLine($"Refused: {exception.Message}");
            return 1;
        }

        var utc = (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime();
        utc = new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var answer = new OwnerAnswer(key, value, reason, utc);
        var applied = OwnerAnswerApplier.Apply(model, [answer]);
        if (applied.AnsweredFacts.Count == 0)
        {
            output.WriteLine($"Refused: {key}: {applied.StaleAnswers[0].Reason}");
            return 1;
        }

        var (answers, error) = ReadAnswersForOutput(directory);
        if (error is not null)
        {
            output.WriteLine($"Owner answers unreadable: {error}");
            return 1;
        }
        OwnerAnswerFile.Write(directory, [.. answers, answer]);
        output.WriteLine($"Recorded owner answer: {key} = {value} (High, {OwnerAnswerApplier.FormatRecorded(utc)})");
        return 0;
    }

    internal static (IReadOnlyList<OwnerAnswer> Answers, string? Error) ReadAnswersForOutput(string directory)
    {
        try
        {
            return (OwnerAnswerFile.Read(directory), null);
        }
        catch (InvalidDataException exception)
        {
            return (Array.Empty<OwnerAnswer>(), exception.Message);
        }
    }

    internal static void WriteAnswerLines(TextWriter output, AppliedOwnerAnswers applied)
    {
        foreach (var answer in applied.AnsweredFacts)
            output.WriteLine($"Owner answer: {answer.FactKey} = {answer.Fact.Value} (High, {answer.Reason})");
        foreach (var stale in applied.StaleAnswers)
            output.WriteLine($"Stale owner answer: {stale.Answer.FactKey} = {stale.Answer.Value} ({stale.Reason})");
    }

    private static (string? Name, string Key, string Value, string? Reason) ParseOptions(IReadOnlyList<string> parts)
    {
        var arguments = new List<string>();
        string? reason = null;
        var hasReason = false;
        for (var index = 2; index < parts.Count; index++)
        {
            var argument = parts[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                arguments.Add(argument);
                continue;
            }
            var equals = argument.IndexOf('=');
            var flag = equals < 0 ? argument : argument[..equals];
            if (flag != "--reason" || hasReason)
                throw new ArgumentException(Usage);
            reason = equals < 0 ? ++index < parts.Count ? parts[index] : null : argument[(equals + 1)..];
            if (reason is null || reason.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            hasReason = true;
        }

        return arguments.Count switch
        {
            2 => (null, arguments[0], arguments[1], reason),
            3 => (arguments[0], arguments[1], arguments[2], reason),
            _ => throw new ArgumentException(Usage)
        };
    }
}
