using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintBriefLintFindings(IReadOnlyList<BriefLintFinding> findings)
    {
        foreach (var finding in findings)
            Console.WriteLine($"BRIEF-LINT {finding.SeverityToken} {finding.Kind}: {finding.Message} remedy: {finding.Remedy}");
    }
}
