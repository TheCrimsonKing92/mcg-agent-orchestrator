using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class HumanWaitsTextView
{
    internal static void Print(
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

}
