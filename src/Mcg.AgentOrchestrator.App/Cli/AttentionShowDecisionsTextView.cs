using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionShowDecisionsTextView
{
    internal static void PrintThenDuties(OrchestratorWorkspace workspace, IReadOnlyCollection<Goal> goals)
    {
        var decisions = OwnerDecisionReader.ReadAsync(OwnerQuestionReadModelFactory.ForWorkspace(workspace))
            .GetAwaiter().GetResult();
        var now = TimeProvider.System.GetUtcNow();
        Console.WriteLine($"Decisions ({decisions.Entries.Count}):");
        if (decisions.Entries.Count == 0) Console.WriteLine("  none");
        foreach (var entry in decisions.Entries)
        {
            TimeSpan? age = entry.RaisedAt is { } raisedAt
                ? TimeSpan.FromTicks(Math.Max(0, (now - raisedAt).Ticks)) : null;
            var text = entry.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\n    ");
            Console.WriteLine($"  [{entry.Kind}] {entry.GoalPrefix} item={entry.ItemId} age={OwnerConsoleBoardRenderer.Age(age)}: {text}");
        }
        var reasons = decisions.HiddenByReason.Count == 0 ? "" :
            $" ({string.Join(", ", decisions.HiddenByReason.Select(pair => $"{pair.Key}={pair.Value}"))})";
        Console.WriteLine($"Hidden decisions: {decisions.Snapshot.Hidden.Count}{reasons}");
        Console.WriteLine();
        PostLandingDutiesTextView.Print(goals);
    }
}
