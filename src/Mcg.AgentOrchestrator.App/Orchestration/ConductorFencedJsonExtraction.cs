using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorFencedJsonExtraction
{
    private static readonly Regex FencePattern = new(@"```(?:json)?\s*(\{[\s\S]*?\})\s*```",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> FencedObjects(string output) =>
        FencePattern.Matches(output).Select(match => match.Groups[1].Value).ToArray();

    internal static string LastFencedOrTrimmed(string output)
    {
        var fenced = FencedObjects(output);
        return fenced.Count == 0 ? output.Trim() : fenced[^1];
    }
}
