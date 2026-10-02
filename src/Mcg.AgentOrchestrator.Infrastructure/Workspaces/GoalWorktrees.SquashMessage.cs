using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    private static string ComposeSquashCommitMessage(
        string executionDirectory, string baseBranch, string mainHead, string oldHead, string tree, GoalId goalId)
    {
        var range = $"{mainHead}..{oldHead}";
        var prefix = $"Developer({Prefix(goalId)}):";
        var subjects = GitCli.Run(executionDirectory, "log", "--format=%s", range);
        var newestDeveloperSubject = subjects.Succeeded && !subjects.DrainTimedOut
            ? subjects.Output.Split('\n').Select(line => line.TrimEnd('\r'))
                .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
            : null;
        var subject = newestDeveloperSubject is null
            ? "squashed goal branch"
            : newestDeveloperSubject[prefix.Length..];
        if (subject.StartsWith(' ')) subject = subject[1..];

        var message = new StringBuilder($"{prefix} {subject}\n\nSquashed onto {baseBranch} from {oldHead} (tree {tree}).");
        var commits = GitCli.Run(executionDirectory, "log", "--reverse", "--no-merges", "--format=%h %s", range);
        if (commits.Succeeded && !commits.DrainTimedOut)
        {
            foreach (var line in commits.Output.Split('\n').Select(line => line.TrimEnd('\r')))
            {
                if (line.Length > 0) message.Append('\n').Append("- ").Append(line);
            }
        }

        return message.Append("\n\nGoal: ").Append(goalId.Value).ToString();
    }
}
