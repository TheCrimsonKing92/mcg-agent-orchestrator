using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalCreationSummaryView
{
    internal const int MaximumTasks = 10;
    internal const int MaximumTitleCharacters = 120;

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Skip(1).Any(arg => arg.Equals("--summary", StringComparison.OrdinalIgnoreCase));

    public static void Print(Goal goal, string outcome, string portfolioPath)
    {
        Console.WriteLine($"Outcome: {outcome}");
        Console.WriteLine($"Goal: {goal.Id.Value}");
        Console.WriteLine($"Status: {goal.Status}");
        Console.WriteLine($"Pipeline: {ReadPipeline(goal)}");
        if (!string.IsNullOrWhiteSpace(goal.SourceBacklogItemId))
        {
            Console.WriteLine($"Source backlog: {goal.SourceBacklogItemId}");
            Console.WriteLine($"Source backlog coverage: {goal.SourceBacklogCoverage?.ToString().ToLowerInvariant() ?? "legacy-full"}");
        }
        var membership = GoalPortfolioMembershipReader.Read(portfolioPath, goal.Id.Value);
        if (membership is not null)
        {
            Console.WriteLine($"Epic: {membership.EpicId}");
            if (membership.ProjectId is not null)
                Console.WriteLine($"Project: {membership.ProjectId}");
        }
        Console.WriteLine($"Tasks: {goal.Tasks.Count}");
        foreach (var task in goal.Tasks.Take(MaximumTasks))
            Console.WriteLine($"  {task.Id.Value} [{task.Status}] {task.RequiredRole}: {ShortTitle(task.Description)}");
        if (goal.Tasks.Count > MaximumTasks)
            Console.WriteLine($"  {goal.Tasks.Count - MaximumTasks} tasks omitted.");
        foreach (var warning in goal.Timeline.Where(evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
                     evt.Message.StartsWith("Brief capability warning:", StringComparison.Ordinal)))
            Console.WriteLine(warning.Message);
    }

    public static void PrintBlocked(GoalObjectivePlan plan)
    {
        Console.WriteLine("Outcome: blocked");
        Console.WriteLine($"Pipeline: {plan.PipelineDecision.Workflow}");
        Console.WriteLine(plan.Recommendation);
        foreach (var warning in plan.CapabilityWarnings)
            Console.WriteLine(warning);
    }

    public static void PrintProgress(GoalIntakeRequestRecord record)
    {
        Console.WriteLine($"Intake: {record.RequestKey}");
        Console.WriteLine($"Outcome: {record.State}");
    }

    private static string ReadPipeline(Goal goal)
    {
        var decision = goal.Timeline.LastOrDefault(evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith("Intake pipeline decision (", StringComparison.Ordinal))?.Message;
        if (decision is null) return "unavailable";
        var start = decision.IndexOf("): ", StringComparison.Ordinal);
        var end = decision.IndexOf(';', start + 3);
        return start >= 0 && end > start + 3 ? decision[(start + 3)..end] : "unavailable";
    }

    private static string ShortTitle(string title)
    {
        var singleLine = string.Join(" ", title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var characters = singleLine.EnumerateRunes().Take(MaximumTitleCharacters + 1).ToArray();
        return characters.Length <= MaximumTitleCharacters
            ? singleLine
            : string.Concat(characters.Take(MaximumTitleCharacters - 1)) + "…";
    }
}
