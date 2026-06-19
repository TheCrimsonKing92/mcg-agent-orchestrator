using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintAttentionQueue(IReadOnlyList<CollaborationItem> items)
    {
        Console.WriteLine();
        Console.WriteLine($"Attention queue: {items.Count} open reach-up item(s)");

        if (items.Count == 0)
        {
            Console.WriteLine("  none");
            Console.WriteLine();
            return;
        }

        foreach (var item in items)
        {
            var goal = item.GoalId is null ? "cross-goal" : item.GoalId[..Math.Min(8, item.GoalId.Length)];
            Console.WriteLine($"  [{item.Type}] {item.Id[..8]} status={item.Status} goal={goal}");
            Console.WriteLine($"    subject: {item.Subject}");
            if (!string.IsNullOrWhiteSpace(item.Body))
                Console.WriteLine($"    body: {item.Body}");
            Console.WriteLine($"    raised: {item.RaisedAt:u}");
        }

        Console.WriteLine();
    }
}
