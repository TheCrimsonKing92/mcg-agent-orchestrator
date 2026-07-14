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
            var verdict = round.OutcomeVerdict?.ToString() ?? "Unpaired";
            Console.WriteLine(
                $"  round {round.RoundNumber}: dispatch={round.DispatchAt:u} " +
                $"prep={FormatDuration(round.SandboxPrepDuration)} worker={FormatDuration(round.WorkerRunDuration)} " +
                $"handoff={FormatDuration(round.HandoffWait)} value={round.ValueClass} verdict={verdict} " +
                $"evidence={OutputTextPreview.CreateSummary(round.ValueEvidence).Text}");
        }
    }

    Console.WriteLine(
        $"Summary: total={FormatDuration(report.TotalDuration)} " +
        $"work={FormatDuration(report.WorkDuration)} ({report.WorkPercent:P0}) " +
        $"wait={FormatDuration(report.WaitDuration)} ({report.WaitPercent:P0}) " +
        $"gate={FormatDuration(report.GateDuration)} landingWait={FormatDuration(report.LandingWait)}");
    Console.WriteLine();
}

public static void PrintDispatchValueReport(DispatchValueReportSnapshot report)
{
    Console.WriteLine();
    var window = report.Since is null ? "all time" : $"since {report.Since.Value:u}";
    Console.WriteLine($"Dispatch value report ({window}): goals={report.GoalCount} rounds={report.DispatchRoundCount}");
    Console.WriteLine("Goal      Outcome    Rounds  Productive  Corrective  EnvWaste  FalseFail  Superseded");
    foreach (var goal in report.Goals)
    {
        Console.WriteLine(
            $"{goal.GoalId.Value[..8],-8} {goal.TerminalOutcome,-10} {goal.DispatchRoundCount,6} " +
            $"{goal.ProductiveCount,10} {goal.CorrectiveCount,10} {goal.WastedEnvironmentalCount,9} " +
            $"{goal.WastedFalseFailCount,9} {goal.SupersededCount,10}");
        foreach (var round in goal.Rounds)
        {
            var verdict = round.OutcomeVerdict?.ToString() ?? "Unpaired";
            Console.WriteLine(
                $"  task={round.TaskId.Value[..8]} round={round.RoundNumber} value={round.ValueClass} " +
                $"verdict={verdict} source={FormatWasteSource(round)} evidence={OutputTextPreview.CreateSummary(round.ValueEvidence).Text}");
        }
    }

    if (report.TopWasteSources.Count > 0)
    {
        Console.WriteLine("Top waste sources:");
        foreach (var source in report.TopWasteSources)
        {
            Console.WriteLine($"  {source.Source}: {source.Count}");
        }
    }

    Console.WriteLine();
}

private static string FormatWasteSource(GoalTimingRoundReport round) =>
    string.IsNullOrWhiteSpace(round.WasteSource) ? "none" : round.WasteSource;
}
