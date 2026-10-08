using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliSinceArgument
{
    internal static bool TryParse(string? value, DateTimeOffset now, out DateTimeOffset cutoff)
    {
        cutoff = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var hours = value.EndsWith("h", StringComparison.OrdinalIgnoreCase);
        if (hours || value.EndsWith("d", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
                || amount <= 0)
                return false;
            try
            {
                cutoff = hours ? now.AddHours(-amount) : now.AddDays(-amount);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out cutoff);
    }

    internal static DateTimeOffset? ParseOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (TryParse(value, DateTimeOffset.UtcNow, out var cutoff))
            return cutoff;
        throw new ArgumentException("Invalid --since value. Use an ISO date/time or a positive Nh or Nd value such as 24h or 14d.");
    }
}
