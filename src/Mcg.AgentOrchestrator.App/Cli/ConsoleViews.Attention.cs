using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintHumanWaits(
        IReadOnlyList<HumanInputRequest> requests,
        DateTimeOffset now,
        bool includeHistory = false, IReadOnlyCollection<Goal>? goals = null)
    {
        foreach (var goal in goals ?? [])
            foreach (var item in goal.GateTestObligations.Where(item => item.BriefVersion == goal.AuthoritativeBrief.Version))
                Console.WriteLine($"  Gate evidence: identity={System.Text.Json.JsonSerializer.Serialize(item.PlannerIdentity)} goal={goal.Id} owner=acceptance-gate availability=post-implementation next=Acceptance executes [{string.Join(", ", item.Selectors)}]");
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
            if (request.PlannerEvidence is { } payload)
                Console.WriteLine($"    evidence: identity={System.Text.Json.JsonSerializer.Serialize(request.PlannerIdentity)} owner={request.EvidenceOwner ?? "operator"} availability={payload.Availability} next={request.ResumeCommand}");
            var resumeCommand = string.Equals(
                request.ResumeCommand,
                HumanInputRequest.BuildDefaultResumeCommand(request.Id),
                StringComparison.Ordinal)
                ? $"attention answer {request.GoalId.Value[..8]} {request.Id.Value[..8]} <answer>"
                : request.ResumeCommand;
            Console.WriteLine($"    resume: {resumeCommand}");
        }

        Console.WriteLine();
    }

    public static void PrintAttentionQueue(IReadOnlyList<CollaborationItem> items)
    {
        PrintCollaborationItems(items, includeHistory: false);
    }

    public static void PrintCollaborationItems(
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
