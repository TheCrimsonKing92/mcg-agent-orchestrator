using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintHumanWaits(
        IReadOnlyList<HumanInputRequest> requests,
        DateTimeOffset now,
        bool includeHistory = false)
    {
        Console.WriteLine();
        Console.WriteLine(includeHistory
            ? $"Human waits: {requests.Count} wait(s)"
            : $"Human waits: {requests.Count} active wait(s)");

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
            if (request.IsCompleted)
            {
                var status = request.WasDismissed ? "dismissed" : "answered";
                Console.WriteLine($"    status: {status} at {request.AnsweredAt:u}");
                if (!string.IsNullOrWhiteSpace(request.Answer))
                    Console.WriteLine($"    resolution: {request.Answer}");
            }
            Console.WriteLine($"    flags: auto-defaultable={request.IsAutoDefaultable}; dismissible={request.IsDismissible}; answer-required={request.IsAnswerRequired}; externally-blocked={request.IsExternallyBlocked}");
            Console.WriteLine($"    question: {request.Question}");
            Console.WriteLine($"    resume: {request.ResumeCommand}");
        }

        Console.WriteLine();
    }

    public static void PrintAttentionQueue(IReadOnlyList<CollaborationItem> items)
    {
        PrintCollaborationItems(items, includeHistory: false);
    }

    public static void PrintCollaborationItems(IReadOnlyList<CollaborationItem> items, bool includeHistory)
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
            Console.WriteLine($"  [{item.Type}] {item.Id[..8]} status={item.Status} goal={goal}");
            Console.WriteLine($"    subject: {item.Subject}");
            if (!string.IsNullOrWhiteSpace(item.Body))
                Console.WriteLine($"    body: {item.Body}");
            if (!string.IsNullOrWhiteSpace(item.Resolution))
                Console.WriteLine($"    resolution: {item.Resolution}");
            Console.WriteLine($"    raised: {item.RaisedAt:u}");
            if (item.ResolvedAt is not null)
                Console.WriteLine($"    resolved: {item.ResolvedAt:u}");
        }

        Console.WriteLine();
    }
}
