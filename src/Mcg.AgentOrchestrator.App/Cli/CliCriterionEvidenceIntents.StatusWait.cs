using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCriterionEvidenceIntents
{
    internal static int PrintStatus(IReadOnlyList<string> args, IOperatorIntentStore store,
        TextWriter output, TimeProvider clock, TimeSpan pollInterval, Action<TimeSpan> delay)
    {
        var (intentId, wait) = ParseStatusArguments(args);
        if (wait is not null && pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var deadline = wait is { } duration ? clock.GetUtcNow() + duration : (DateTimeOffset?)null;
        while (true)
        {
            var intent = store.GetAsync(intentId).GetAwaiter().GetResult()
                ?? throw new KeyNotFoundException($"Operator intent '{intentId}' was not found.");
            if (deadline is null)
            {
                output.WriteLine(FormatStatusLine(intent));
                return 0;
            }

            if (intent.Status is OperatorIntentStatus.Applied or OperatorIntentStatus.Rejected)
            {
                output.WriteLine(FormatStatusLine(intent));
                return intent.Status == OperatorIntentStatus.Applied ? 0 : 2;
            }

            var remaining = deadline.Value - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                output.WriteLine(FormatStatusLine(intent));
                return 3;
            }

            delay(remaining < pollInterval ? remaining : pollInterval);
        }
    }

    private static (string IntentId, TimeSpan? Wait) ParseStatusArguments(IReadOnlyList<string> args)
    {
        string? intentId = null;
        TimeSpan? wait = null;
        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg.Equals("--wait", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--wait=", StringComparison.OrdinalIgnoreCase))
            {
                if (wait is not null)
                    throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);

                var seconds = 300;
                if (arg.Contains('='))
                {
                    if (!int.TryParse(arg[7..], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                        throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);
                }
                else if (index + 1 < args.Count && int.TryParse(args[index + 1],
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var suppliedSeconds))
                {
                    seconds = suppliedSeconds;
                    index++;
                }

                if (seconds <= 0)
                    throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);
                wait = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                if (intentId is not null || arg.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);
                intentId = arg;
            }
        }

        return (intentId ?? throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage), wait);
    }

    private static string FormatStatusLine(OperatorIntentRecord intent) =>
        $"Operator intent {intent.Id}: verb={intent.Verb} goal={intent.GoalId[..Math.Min(8, intent.GoalId.Length)]} " +
        $"task={(intent.TaskId is null ? "none" : intent.TaskId[..Math.Min(8, intent.TaskId.Length)])} " +
        $"status={intent.Status} actor={intent.Actor} channel={intent.Channel} auth={intent.AuthenticationAssurance} " +
        $"outcome={intent.Outcome ?? "pending"}";
}
