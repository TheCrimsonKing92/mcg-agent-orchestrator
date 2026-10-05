namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static Dictionary<string, string> ReadBacklogUpdateValues(IReadOnlyList<string> parts)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 2; index < parts.Count; index += 2)
        {
            var flag = parts[index];
            if (!CliArgumentParser.IsBacklogUpdateValueFlag(flag))
                throw new ArgumentException($"Unexpected backlog-update argument '{flag}'. {CliCommandHelp.BacklogUpdateUsage}");
            if (values.ContainsKey(flag))
                throw new ArgumentException($"Provide {flag} only once.");
            if (index + 1 >= parts.Count || CliArgumentParser.IsBacklogUpdateTerminator(parts[index + 1]))
                throw new ArgumentException($"{flag} requires a value. Use --text-file for descriptions containing literal option names.");
            values.Add(flag, parts[index + 1]);
        }
        return values;
    }

    private static string? ResolveBacklogUpdateDescription(
        IReadOnlyList<string> parts, Dictionary<string, string> values, string? description)
    {
        var fileFlags = new[] { "--text-file", "--body-file" };
        foreach (var flag in fileFlags)
        {
            if (description is not null && values.ContainsKey(flag))
                throw new ArgumentException($"Provide either --description or {flag} <path>, not both.");
        }

        try
        {
            return ResolveTextArgumentOrDefault(parts, parts.Count, description, fileFlags);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var flag = fileFlags.First(values.ContainsKey);
            throw new InvalidOperationException($"{flag} could not be read: {values[flag]}", exception);
        }
    }
}
