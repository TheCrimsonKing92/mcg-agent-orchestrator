namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConsoleDurationFormat
{
    internal static string Format(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "n/a";
        }

        var value = duration.Value;
        return value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#}m"
            : $"{value.TotalSeconds:0.#}s";
    }
}
