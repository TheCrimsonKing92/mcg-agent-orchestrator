using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCriterionEvidenceIntents
{
    internal static int PrintStatus(IReadOnlyList<string> args, IOperatorIntentStore store,
        TextWriter output, TimeProvider clock, TimeSpan pollInterval, Action<TimeSpan> delay)
    {
        var (intentIds, wait) = ParseStatusArguments(args);
        if (wait is not null && pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var deadline = wait is { } duration ? clock.GetUtcNow() + duration : (DateTimeOffset?)null;
        var intents = new OperatorIntentRecord?[intentIds.Count];
        while (true)
        {
            for (var index = 0; index < intentIds.Count; index++)
            {
                if (intents[index]?.Status is OperatorIntentStatus.Applied or OperatorIntentStatus.Rejected)
                    continue;
                var intentId = intentIds[index];
                intents[index] = store.GetAsync(intentId).GetAwaiter().GetResult()
                    ?? throw new KeyNotFoundException($"Operator intent '{intentId}' was not found.");
            }

            var pending = intents.Any(intent => intent!.Status is not (OperatorIntentStatus.Applied or OperatorIntentStatus.Rejected));
            var remaining = deadline is null || !pending ? TimeSpan.Zero : deadline.Value - clock.GetUtcNow();
            if (deadline is null || !pending || remaining <= TimeSpan.Zero)
            {
                foreach (var intent in intents)
                    output.WriteLine(FormatStatusLine(intent!));
                return deadline is null ? 0 : intents.Any(intent => intent!.Status == OperatorIntentStatus.Rejected) ? 2 : pending ? 3 : 0;
            }

            delay(remaining < pollInterval ? remaining : pollInterval);
        }
    }

    private static (IReadOnlyList<string> IntentIds, TimeSpan? Wait) ParseStatusArguments(IReadOnlyList<string> args)
    {
        var intentIds = new List<string>();
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
                else if (index + 1 < args.Count && IsIntegerToken(args[index + 1]))
                {
                    if (!int.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                        throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);
                }

                if (seconds <= 0)
                    throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);
                wait = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);
                intentIds.Add(arg);
            }
        }

        if (intentIds.Count == 0)
            throw new ArgumentException(CliCommandHelp.OperatorIntentStatusUsage);
        return (intentIds, wait);
    }

    private static bool IsIntegerToken(string value)
    {
        var digits = value.AsSpan().Trim();
        if (digits.Length > 0 && digits[0] is '+' or '-') digits = digits[1..];
        return digits.Length > 0 && !digits.ContainsAnyExceptInRange('0', '9');
    }

    private static string FormatStatusLine(OperatorIntentRecord intent) =>
        $"Operator intent {intent.Id}: verb={intent.Verb} goal={intent.GoalId[..Math.Min(8, intent.GoalId.Length)]} " +
        $"task={(intent.TaskId is null ? "none" : intent.TaskId[..Math.Min(8, intent.TaskId.Length)])} " +
        $"status={intent.Status} actor={intent.Actor} channel={intent.Channel} auth={intent.AuthenticationAssurance} " +
        $"outcome={intent.Outcome ?? "pending"}";
}
