using System.Net;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static void RenderOperatorControlsScript(StringBuilder html)
    {
        html.AppendLine("<script src=\"/assets/dashboard.js\"></script>");
    }

    private static bool IsYesNoQuestion(string question)
    {
        var trimmed = question.TrimStart();
        var firstWordLength = trimmed.IndexOfAny([' ', '\t', '\r', '\n', '?', ':', ';', ',']);
        var firstWord = firstWordLength < 0 ? trimmed : trimmed[..firstWordLength];
        return firstWord.Equals("Should", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Do", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Does", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Did", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Can", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Could", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Is", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Are", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Was", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Were", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Will", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Would", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Has", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Have", StringComparison.OrdinalIgnoreCase)
            || firstWord.Equals("Had", StringComparison.OrdinalIgnoreCase);
    }
}


