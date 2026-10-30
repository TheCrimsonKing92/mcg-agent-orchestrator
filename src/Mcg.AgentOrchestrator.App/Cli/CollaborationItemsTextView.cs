using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CollaborationItemsTextView
{
    public static void PrintAttentionQueue(IReadOnlyList<CollaborationItem> items)
    {
        Print(items, includeHistory: false);
    }

    public static void Print(
        IReadOnlyList<CollaborationItem> items,
        bool includeHistory,
        Func<CollaborationItem, string?>? itemId = null)
    {
        Console.WriteLine();
        Console.WriteLine(includeHistory
            ? $"Attention queue: {items.Count} reach-up item(s)"
            : $"Attention queue: {items.Count} open reach-up item(s)");

        if (items.Count == 0)
        {
            Console.WriteLine("  none");
            Console.WriteLine();
            return;
        }

        foreach (var item in items)
        {
            var goal = item.GoalId is null ? "cross-goal" : item.GoalId[..Math.Min(8, item.GoalId.Length)];
            var renderedId = itemId?.Invoke(item);
            if (string.IsNullOrWhiteSpace(renderedId))
                renderedId = item.Id[..8];
            Console.WriteLine($"  [{item.Type}] {renderedId} status={item.Status} goal={goal}");
            Console.WriteLine(CollaborationItemLifecycle.IsTerminal(item.Status)
                ? $"    dismiss: unavailable (status={item.Status}; item-id={item.Id})"
                : $"    dismiss: attention dismiss --item {item.Id}");
            Console.WriteLine($"    subject: {item.Subject}");
            if (!string.IsNullOrWhiteSpace(item.Body))
                Console.WriteLine($"    body: {item.Body}");
            if (!string.IsNullOrWhiteSpace(item.Resolution))
                Console.WriteLine($"    resolution: {item.Resolution}");
            if (includeHistory && item.AnswerHistory is { Count: > 0 })
            {
                foreach (var answer in item.AnswerHistory.OrderBy(answer => answer.AnsweredAt))
                {
                    var state = answer.IsRetracted
                        ? $"retracted supersededBy={answer.SupersededByAnswerId}"
                        : "authoritative";
                    Console.WriteLine($"    answer-history: {answer.Id} {state}: {answer.Text}");
                }
            }
            Console.WriteLine($"    raised: {item.RaisedAt:u}");
            if (item.ResolvedAt is not null)
                Console.WriteLine($"    resolved: {item.ResolvedAt:u}");
        }

        Console.WriteLine();
    }
}
