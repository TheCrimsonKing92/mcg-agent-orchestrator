using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    internal const string OutputSilenceBudgetEnvironmentVariable =
        "MCG_ORCHESTRATOR_OUTPUT_SILENCE_BUDGET_MINUTES";
    private readonly string? _configuredOutputSilenceBudget =
        Environment.GetEnvironmentVariable(OutputSilenceBudgetEnvironmentVariable);
    private TimeSpan _outputSilenceBudget = TimeSpan.FromMinutes(10);

    internal static TimeSpan ResolveOutputSilenceBudget(string? environmentValue, int pollSeconds)
    {
        var configured = int.TryParse(environmentValue?.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : TimeSpan.FromMinutes(10);
        var pollFloor = TimeSpan.FromSeconds(3.0 * pollSeconds + 60);
        return configured > pollFloor ? configured : pollFloor;
    }

    private void InitializeOutputSilenceBudget(IReadOnlyList<string> args)
    {
        if (outputSilenceBudget is { } injected)
        {
            _outputSilenceBudget = injected;
            return;
        }

        int pollSeconds;
        try
        {
            pollSeconds = CliCommandHandlers.ResolveConductPollSeconds(args);
        }
        catch (ArgumentException)
        {
            pollSeconds = ConductorBatchLoop.DefaultWatchIntervalSeconds;
        }

        _outputSilenceBudget = ResolveOutputSilenceBudget(_configuredOutputSilenceBudget, pollSeconds);
    }
}
