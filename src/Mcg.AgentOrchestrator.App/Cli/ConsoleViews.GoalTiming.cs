using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintGoalTimingReport(GoalTimingReportSnapshot report)
{
    Console.WriteLine();
    Console.WriteLine($"Goal timing {report.GoalId.Value[..8]}: {OutputTextPreview.CreateSummary(report.Objective).Text}");
    if (report.CurrentHold is { } hold)
    {
        Console.WriteLine(
            $"Current status: held state={hold.State} since={hold.StartedAt:u} " +
            $"duration={FormatDuration(hold.Duration)} stalled={(hold.StalledAt is null ? "no" : "yes")} " +
            $"blocker={OutputTextPreview.CreateSummary(hold.Blocker).Text}");
    }
    else
    {
        Console.WriteLine("Current status: progressing (no active hold)");
    }
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
        $"backlogIntentWait={FormatDuration(report.BacklogIntentWait)} " +
        $"intakeWait={FormatDuration(report.IntakeToFirstDispatchWait)} " +
        $"gate={FormatDuration(report.GateDuration)} landingWait={FormatDuration(report.LandingWait)} " +
        $"landedAt={(report.LandedAt is null ? "n/a" : report.LandedAt.Value.ToString("u"))} " +
        $"source={report.LandingSource ?? "timeline"}");
    Console.WriteLine();
}

public static void PrintGoalTimingRollup(GoalTimingRollupSnapshot report)
{
    Console.WriteLine();
    Console.WriteLine(
        $"Goal timing rollup: goals={report.GoalCount} total={FormatDuration(report.TotalDuration)} " +
        $"work={FormatDuration(report.WorkDuration)} ({report.WorkPercent:P0}) " +
        $"wait={FormatDuration(report.WaitDuration)} ({report.WaitPercent:P0}) " +
        $"productive={report.ProductiveCount} corrective={report.CorrectiveCount} " +
        $"envWaste={report.WastedEnvironmentalCount} falseFail={report.WastedFalseFailCount} superseded={report.SupersededCount}");
    Console.WriteLine("Phase               Total   Share  Median     P90");
    foreach (var phase in report.Phases)
    {
        Console.WriteLine(
            $"{phase.Phase,-18} {FormatDuration(phase.Total),7} {phase.Share,7:P0} " +
            $"{FormatDuration(phase.Median),7} {FormatDuration(phase.P90),7}");
    }

    if (report.Daily.Count > 0)
    {
        Console.WriteLine("Daily trend:");
        Console.WriteLine("Day          Goals    Total  Median  Work%  EnvWaste  FalseFail");
        foreach (var day in report.Daily)
        {
            Console.WriteLine(
                $"{day.Day:yyyy-MM-dd} {day.GoalCount,5} {FormatDuration(day.TotalDuration),8} " +
                $"{FormatDuration(day.MedianTotalDuration),7} {day.WorkPercent,6:P0} " +
                $"{day.WastedEnvironmentalCount,9} {day.WastedFalseFailCount,10}");
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
