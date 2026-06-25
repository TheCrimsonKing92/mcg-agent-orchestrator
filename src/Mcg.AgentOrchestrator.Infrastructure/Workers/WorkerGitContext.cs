namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerGitContext
{
    private const int DiffSummaryMaxChars = 6000;
    internal const int DiffSummaryRetrievalMaxChars = DiffSummaryMaxChars;

    internal string[] ReadChangedFilesForTestImpact(string workingDirectory)
    {
        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            return [];
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryRunGit(workingDirectory, ["diff", "--name-only", "HEAD", "--"], out var diffOutput))
        {
            foreach (var line in SplitGitOutput(diffOutput))
            {
                files.Add(line);
            }
        }

        if (TryRunGit(workingDirectory, ["status", "--short"], out var statusOutput))
        {
            foreach (var line in SplitGitOutput(statusOutput))
            {
                if (line.Length < 4)
                {
                    continue;
                }

                var path = line[3..].Trim();
                var renameIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
                files.Add(renameIndex >= 0 ? path[(renameIndex + 4)..] : path);
            }
        }

        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] SplitGitOutput(string output) =>
        output
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal string BuildDiffSummary(string workingDirectory)
    {
        var lines = new List<string>
        {
            "# Diff Summary",
            string.Empty,
            $"Working directory: {workingDirectory}",
            "Regeneration: generated at dispatch preparation; treat as stale when git status or HEAD changes after dispatch.",
            string.Empty,
            "## Git Status"
        };

        if (!LooksLikeGitWorkspace(workingDirectory))
        {
            lines.Add("- git data unavailable for this workspace.");
            lines.Add(string.Empty);
            lines.Add("## Changed Files Against HEAD");
            lines.Add("- git data unavailable for this workspace.");
            lines.Add(string.Empty);
            lines.Add("## Diff Stat Against HEAD");
            lines.Add("- git data unavailable for this workspace.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.AddRange(ReadGitSection(workingDirectory, ["status", "--short"]));
        lines.Add(string.Empty);
        lines.Add("## Changed Files Against HEAD");
        lines.AddRange(ReadGitSection(workingDirectory, ["diff", "--name-status", "HEAD", "--"]));
        lines.Add(string.Empty);
        lines.Add("## Diff Stat Against HEAD");
        lines.AddRange(ReadGitSection(workingDirectory, ["diff", "--stat", "HEAD", "--"]));

        return WorkerContextHelpers.TrimArtifactBlock(string.Join(Environment.NewLine, lines), DiffSummaryMaxChars);
    }

    private static bool LooksLikeGitWorkspace(string workingDirectory)
    {
        return Directory.Exists(Path.Combine(workingDirectory, ".git")) ||
            File.Exists(Path.Combine(workingDirectory, ".git"));
    }

    private static List<string> ReadGitSection(string workingDirectory, string[] arguments)
    {
        if (!TryRunGit(workingDirectory, arguments, out var output))
        {
            return ["- git data unavailable for this workspace."];
        }

        var lines = output
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(80)
            .Select(line => $"- {line}")
            .ToList();
        return lines.Count == 0 ? ["- clean"] : lines;
    }

    private static bool TryRunGit(string workingDirectory, string[] arguments, out string output)
    {
        var result = GitCli.Run(workingDirectory, 5_000, arguments);
        output = result.Output;
        return result.Succeeded;
    }
}
