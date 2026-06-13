using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintHistoricalDogfoodEvaluation(HistoricalDogfoodEvaluationReport report)
    {
        Console.WriteLine($"Dogfood evaluation {report.GoalPrefix} {report.Status}: {OutputTextPreview.CreateSummary(report.Objective).Text}");
        Console.WriteLine($"Score: {report.Score}/100 ({report.Grade})");
        Console.WriteLine("Metrics:");
        foreach (var metric in report.Metrics)
        {
            var sign = metric.Penalty > 0 ? "-" : metric.Penalty < 0 ? "+" : "";
            Console.WriteLine($"  {metric.Name}: value={metric.Value}, score {sign}{Math.Abs(metric.Penalty)}");
            Console.WriteLine($"    {OutputTextPreview.CreateTimeline(metric.Detail).Text}");
        }

        Console.WriteLine("Recommendations:");
        if (report.Recommendations.Count == 0)
        {
            Console.WriteLine("  none");
            return;
        }

        foreach (var recommendation in report.Recommendations)
        {
            Console.WriteLine($"  - {OutputTextPreview.CreateTimeline(recommendation).Text}");
        }
    }
}
