using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintHumanWaits(IReadOnlyList<HumanInputRequest> requests, DateTimeOffset now)
    {
        Console.WriteLine();
        Console.WriteLine($"Human waits: {requests.Count} active wait(s)");

        if (requests.Count == 0)
        {
            Console.WriteLine("  none");
            Console.WriteLine();
            return;
        }

        foreach (var request in requests)
        {
            var ageSeconds = Math.Max(0, (long)(now - request.CreatedAt).TotalSeconds);
            Console.WriteLine($"  [{request.Kind}] {request.Id.Value[..8]} goal={request.GoalId.Value[..8]} task={request.TaskId?.Value[..8] ?? "goal"} age={ageSeconds}s");
            Console.WriteLine($"    flags: auto-defaultable={request.IsAutoDefaultable}; dismissible={request.IsDismissible}; answer-required={request.IsAnswerRequired}; externally-blocked={request.IsExternallyBlocked}");
            Console.WriteLine($"    question: {request.Question}");
            Console.WriteLine($"    resume: {request.ResumeCommand}");
        }

        Console.WriteLine();
    }

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
