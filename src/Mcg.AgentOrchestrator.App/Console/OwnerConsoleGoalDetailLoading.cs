namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetailLoading
{
    internal const string LoadingLine = "Loading goal detail...";
    internal const string UnavailableText = "goal state unavailable";

    internal static OwnerConsoleGoalDialog.Content Placeholder(string goalId, OwnerConsoleBoardRow? row) =>
        new(row is null
            ? string.Join(Environment.NewLine, goalId, LoadingLine)
            : string.Join(Environment.NewLine, $"{row.GoalPrefix}  {row.Title}",
                $"State: {OwnerConsoleStatusLabel.For(row.State)}", $"Stage: {row.Stage}", LoadingLine), [], [], null, null, null);

    internal static OwnerConsoleGoalDialog.Content Unavailable(Exception? failure) =>
        new(failure is null ? UnavailableText
            : UnavailableText + Environment.NewLine + "Load failed: " + failure.Message,
            [], [], null, null, null);
}
