using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceCheckTimeouts
{
    internal const string EnvironmentVariable = "MCG_ACCEPTANCE_CHECK_TIMEOUT_MINUTES";
    internal const string AppContextKey = "Mcg.AgentOrchestrator.AcceptanceCheckTimeoutMinutes";
    internal static readonly TimeSpan FallbackDefault = TimeSpan.FromMinutes(25);

    public static TimeSpan DefaultTimeout => Resolve(null);

    public static TimeSpan Resolve(int? timeoutMinutes)
    {
        if (timeoutMinutes is > 0)
        {
            return TimeSpan.FromMinutes(timeoutMinutes.Value);
        }

        return TryParseMinutes(Environment.GetEnvironmentVariable(EnvironmentVariable))
            ?? TryParseAppContextMinutes(AppContextKey)
            ?? TryParseAppContextMinutes(EnvironmentVariable)
            ?? FallbackDefault;
    }

    private static TimeSpan? TryParseAppContextMinutes(string key)
    {
        var value = AppContext.GetData(key);
        return value switch
        {
            TimeSpan timeSpan when timeSpan > TimeSpan.Zero => timeSpan,
            int minutes when minutes > 0 => TimeSpan.FromMinutes(minutes),
            long minutes when minutes > 0 => TimeSpan.FromMinutes(minutes),
            double minutes when minutes > 0 => TimeSpan.FromMinutes(minutes),
            string text => TryParseMinutes(text),
            _ => null
        };
    }

    private static TimeSpan? TryParseMinutes(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) &&
            minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : null;
    }
}
