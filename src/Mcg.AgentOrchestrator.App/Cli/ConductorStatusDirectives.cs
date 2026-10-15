using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStatusDirectives
{
    internal static void Print(OrchestratorWorkspace workspace, TextWriter output, TimeProvider time)
    {
        string line;
        try
        {
            var now = time.GetUtcNow();
            var active = new SqliteOperatorDirectiveStore(workspace.OperatorDirectivesStorePath)
                .List(now).Where(row => !row.Expired).ToArray();
            line = $"Owner directives: {active.Length} active";
            if (active.Length is >= 1 and <= 3)
                line += ": " + string.Join("; ", active.Select(row => $"{row.Kind}: {FirstLine(row.Text)}"));
        }
        catch (Exception) { line = "Owner directives: unavailable"; }
        output.WriteLine(line);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? text : text[..end]).Trim();
        return line.Length <= 80 ? line : line[..80];
    }
}
