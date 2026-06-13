using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintOperatorInbox(OperatorInboxReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"Operator inbox: {report.OpenCount} open; {report.AcknowledgedCount} acknowledged");
        if (!string.IsNullOrWhiteSpace(report.GoalPrefix))
        {
            Console.WriteLine($"Goal filter: {report.GoalPrefix}");
        }

        if (report.Items.Count == 0)
        {
            Console.WriteLine("  none");
            Console.WriteLine();
            return;
        }

        foreach (var item in report.Items)
        {
            var workItem = item.TaskNumber is null ? "goal" : $"task {item.TaskNumber}";
            var ack = item.Acknowledged ? " acknowledged" : string.Empty;
            Console.WriteLine($"  {item.Id} [{item.Severity}] {item.Kind}{ack} {item.GoalPrefix}/{workItem}");
            Console.WriteLine($"     title: {OutputTextPreview.CreateSummary(item.Title).Text}");
            Console.WriteLine($"     message: {OutputTextPreview.CreateTimeline(item.Message).Text}");
            Console.WriteLine($"     evidence: {OutputTextPreview.CreateTimeline(item.Evidence).Text}");
            Console.WriteLine($"     action: {OutputTextPreview.CreateTimeline(item.SuggestedAction).Text}");
            Console.WriteLine($"     command: {OutputTextPreview.CreateTimeline(item.SuggestedCommand).Text}");
        }

        Console.WriteLine();
    }
}
