using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class FindingLookupTextView
{
    public static void PrintFindingLookup(FindingLookupResult result)
    {
        Console.WriteLine($"{result.Matches.Count} finding occurrence(s) match '{result.Query}'.");
        foreach (var match in result.Matches)
        {
            Console.WriteLine($"Goal {match.GoalId.Value}, task {match.TaskNumber} ({match.TaskId.Value}), role {match.Role}");
            Console.WriteLine($"  Reviewed commit: {match.ReviewedCommit ?? "none"}; completed: {match.CompletedAt:O}");
            Console.WriteLine($"  {match.Finding.StableId}: {match.Finding.Severity}, {match.Finding.State}, {match.Finding.Category}");
            Console.WriteLine($"  Location: {match.Finding.Location}");
            Console.WriteLine($"  Description: {match.Finding.Description}");
            foreach (var receipt in match.Receipts)
            {
                Console.WriteLine($"  Receipt {receipt.ReceiptId}: candidate {receipt.CandidateSha}, Accepted={receipt.Accepted}, Passed={receipt.Passed}, Summary={receipt.Summary}");
                foreach (var arm in receipt.Arms ?? [])
                    Console.WriteLine($"    Arm {arm.Arm}: sha {arm.Sha}, {arm.Disposition}, Accepted={arm.Accepted}, Passed={arm.Passed}, Summary={arm.Summary}");
            }
            if (match.MissingReceiptReason is { } reason)
                Console.WriteLine($"  {reason}");
            else if (match.Finding.EvidenceRequest is null && match.Receipts.Count == 0)
                Console.WriteLine("  no evidence requested");
        }
    }
}
