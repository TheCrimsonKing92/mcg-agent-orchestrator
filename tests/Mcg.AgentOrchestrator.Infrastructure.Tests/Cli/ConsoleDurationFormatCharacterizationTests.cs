using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Xunit;

// Parallel-safe: async-context culture and local fixtures; no process-global state.
// Expected strings derive from ConsoleViews.TaskDurations.cs::FormatDuration at base
// 3c9cba3f484b78afc3ccb5a7e7a04d5c33dc0d4b.
public sealed class ConsoleDurationFormatCharacterizationTests
{
    [Theory]
    [InlineData(null, "n/a")]
    [InlineData(0.0, "0s")]
    [InlineData(42.5, "42.5s")]
    [InlineData(60.0, "1m")]
    [InlineData(9000.0, "150m")]
    [InlineData(95400.0, "1590m")]
    public void Format_DurationBoundaries_PreservesExactText(double? seconds, string expected)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            TimeSpan? duration = seconds is null ? null : TimeSpan.FromSeconds(seconds.Value);

            Assert.Equal(expected, ConsoleDurationFormat.Format(duration));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
