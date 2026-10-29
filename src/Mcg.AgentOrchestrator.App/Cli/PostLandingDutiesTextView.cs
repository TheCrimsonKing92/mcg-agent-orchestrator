using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class PostLandingDutiesTextView
{
    internal static void Print(IReadOnlyCollection<Goal> goals)
    {
        foreach (var goal in goals)
        foreach (var duty in goal.OutstandingPostLandingDuties)
        {
            Console.WriteLine($"Post-landing duty: goal={goal.Id.Value} obligation={duty.Id} {duty.DisplayLabel} phase={duty.Phase} state={duty.State} owner={duty.Owner}");
            Console.WriteLine($"  artifact={duty.Publication?.CandidateRevision ?? duty.ExpectedCandidateSha ?? "publication pending"} condition={duty.ObservationScope?.Condition} target={duty.ObservationScope?.Target}");
            Console.WriteLine($"  next: criterion-evidence-record --goal {goal.Id.Value} {duty.Id} operator <original-candidate> <receipt-id> \"{duty.RequiredScope}\" passed <detail> --observation-json <attributed-activation-and-observation>");
        }
    }
}
