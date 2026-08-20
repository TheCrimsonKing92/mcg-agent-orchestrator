using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalBoardGitDivergence
{
    internal const int DirtyProbeTimeoutMilliseconds = 5_000;

    internal static IReadOnlyDictionary<string, GitCli.AheadBehindInspection> Capture(
        string executionDirectory,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        gitRunner ??= (workingDirectory, args) => GitCli.Run(workingDirectory, args.ToArray());
        var result = gitRunner(
            Path.GetFullPath(executionDirectory),
            ["for-each-ref", "--format=%(refname:short) %(ahead-behind:main)", "refs/heads/goal/"]);
        if (!result.Succeeded || result.DrainTimedOut)
            return Empty();

        return Parse(result.Output);
    }

    internal static IReadOnlyDictionary<string, GitCli.AheadBehindInspection> Parse(string output)
    {
        var snapshot = Empty();
        if (string.IsNullOrWhiteSpace(output))
            return snapshot;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ahead) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var behind))
            {
                continue;
            }

            snapshot[parts[0]] = new GitCli.AheadBehindInspection(true, ahead, behind, null);
        }

        return snapshot;
    }

    private static Dictionary<string, GitCli.AheadBehindInspection> Empty() =>
        new(StringComparer.Ordinal);
}
