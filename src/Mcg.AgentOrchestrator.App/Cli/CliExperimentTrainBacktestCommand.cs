using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliExperimentTrainBacktestCommand
{
    internal static bool Execute(IReadOnlyList<string> parts, OrchestratorWorkspace workspace)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < parts.Count; i += 2)
        {
            if (parts[i] is not ("--since" or "--training-count"))
                throw new ArgumentException($"Unknown option: {parts[i]}");
            if (i + 1 >= parts.Count || !options.TryAdd(parts[i], parts[i + 1]))
                throw new ArgumentException($"{parts[i][2..]}: expected one value.");
        }
        if (!options.TryGetValue("--since", out var text) ||
            !(text.EndsWith('Z') || (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':')) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var since))
            throw new ArgumentException("since: expected a timestamp with an explicit offset.");
        var trainingCount = 100;
        if (options.TryGetValue("--training-count", out var count) &&
            (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out trainingCount) || trainingCount <= 0))
            throw new ArgumentException("training-count: expected a positive whole number.");
        var store = MergeTrainAcceptanceStore.OpenReadOnly(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
        Console.WriteLine($"merge train drop backtest since {since.ToString("O", CultureInfo.InvariantCulture)}");
        foreach (var line in MergeTrainDropBacktest.Run(store.ReadReceiptsSince(since), trainingCount)) Console.WriteLine(line);
        return false;
    }
}
