using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerLandingsPage(string Text, IReadOnlyList<int> ChoiceLines,
    IReadOnlyList<string> ChoiceGoalIds)
{
    internal static OwnerLandingsPage Plain(string text) => new(text, [], []);

    internal static OwnerLandingsPage From(OwnerLandingsQuery.Result result)
    {
        var lines = OwnerLandingsQuery.Render(result);
        if (result.InvalidWindow || result.Rows.Count == 0) return Plain(string.Join("\n", lines));
        var choiceLines = new List<int>();
        var goalIds = new List<string>();
        for (var i = 0; i < result.Rows.Count; i++)
        {
            choiceLines.Add(1 + 2 * i);
            choiceLines.Add(2 + 2 * i);
            goalIds.Add(result.Rows[i].GoalId);
            goalIds.Add(result.Rows[i].GoalId);
        }
        return new(string.Join("\n", new[] { "Enter on a row opens its goal detail." }.Concat(lines)),
            choiceLines, goalIds);
    }

    internal static OwnerLandingsPage Load(IOrchestratorStateQueries state, string orchestratorDirectory,
        TimeProvider clock, string windowArgument)
    {
        try { return From(OwnerLandingsQuery.Load(state, orchestratorDirectory, clock, windowArgument)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Plain("landings unavailable: " + ex.Message); }
    }
}
