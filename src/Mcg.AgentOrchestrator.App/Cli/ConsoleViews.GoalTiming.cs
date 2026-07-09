using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintGoalTimingReport(GoalTimingReportSnapshot report)
{
    Console.WriteLine();
    Console.WriteLine($"Goal timing {report.GoalId.Value[..8]}: {OutputTextPreview.CreateSummary(report.Objective).Text}");
    Console.WriteLine("Task  Role       Rounds  IntakeWait  Prep  Worker  Handoff");
    foreach (var task in report.Tasks.Where(task => task.Rounds.Count > 0))
    {
        Console.WriteLine(
            $"{task.TaskId.Value[..8],-8} {task.Role,-10} {task.Rounds.Count,6} " +
            $"{FormatDuration(task.IntakeToFirstDispatchWait),10} " +
            $"{FormatDuration(task.Rounds.Aggregate(TimeSpan.Zero, (sum, round) => sum + round.SandboxPrepDuration)),5} " +
            $"{FormatDuration(task.Rounds.Aggregate(TimeSpan.Zero, (sum, round) => sum + round.WorkerRunDuration)),7} " +
            $"{FormatDuration(task.Rounds.Aggregate(TimeSpan.Zero, (sum, round) => sum + round.HandoffWait)),8}");
        foreach (var round in task.Rounds)
        {
            Console.WriteLine(
                $"  round {round.RoundNumber}: dispatch={round.DispatchAt:u} " +
                $"prep={FormatDuration(round.SandboxPrepDuration)} worker={FormatDuration(round.WorkerRunDuration)} " +
                $"handoff={FormatDuration(round.HandoffWait)}");
        }
    }

    Console.WriteLine(
        $"Summary: total={FormatDuration(report.TotalDuration)} " +
        $"work={FormatDuration(report.WorkDuration)} ({report.WorkPercent:P0}) " +
        $"wait={FormatDuration(report.WaitDuration)} ({report.WaitPercent:P0}) " +
        $"gate={FormatDuration(report.GateDuration)} landingWait={FormatDuration(report.LandingWait)}");
    Console.WriteLine();
}
}
