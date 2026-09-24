using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static bool? TryExecuteFlakeCensusCommand(
        string command,
        IReadOnlyList<string> parts,
        CliExecutionContext context)
    {
        if (!command.Equals("flake-census", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var minimumGoals = 2;
        DateTimeOffset? since = null;
        for (var index = 1; index < parts.Count; index += 2)
        {
            if (index + 1 >= parts.Count)
            {
                throw new ArgumentException(CliCommandHelp.FlakeCensusUsage);
            }

            if (parts[index].Equals("--min-goals", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(parts[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out minimumGoals) ||
                    minimumGoals < 1)
                {
                    throw new ArgumentException("--min-goals must be a positive integer. " + CliCommandHelp.FlakeCensusUsage);
                }
            }
            else if (parts[index].Equals("--since", StringComparison.OrdinalIgnoreCase))
            {
                since = ParseFlakeCensusSince(parts[index + 1]);
            }
            else
            {
                throw new ArgumentException(CliCommandHelp.FlakeCensusUsage);
            }
        }

        var indexPath = Path.Combine(
            context.Workspace.OrchestratorDirectory,
            "acceptance-gate-attempts",
            AcceptanceFailingTestIndex.FileName);
        var rows = AcceptanceFailingTestIndex.BuildCensus(
            new AcceptanceFailingTestIndex(indexPath).Read(),
            minimumGoals,
            since);
        foreach (var row in rows)
        {
            Console.WriteLine(
                $"{row.TestIdentity} | distinct-goals={row.DistinctGoals} | " +
                $"outside-changed-paths={row.OutsideChangedPathsFailures} | total-failures={row.TotalFailures} | " +
                $"first-seen={row.FirstSeen.UtcDateTime:yyyy-MM-dd} | last-seen={row.LastSeen.UtcDateTime:yyyy-MM-dd}");
        }

        return false;
    }

    private static DateTimeOffset ParseFlakeCensusSince(string value)
    {
        if (DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var date))
        {
            return date.ToUniversalTime();
        }

        if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return timestamp;
        }

        throw new ArgumentException(
            "--since must be yyyy-MM-dd or a full ISO 8601 timestamp with offset. " +
            CliCommandHelp.FlakeCensusUsage);
    }
}
