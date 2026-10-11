namespace Mcg.AgentOrchestrator.Infrastructure;

// Text matching is retained only for the versioned legacy marked-text ingress.
internal static partial class WorkerContextProjectionResidual
{
    private static string ApplyReviewerPreviewPolicy(
        string content,
        bool removeChangedPaths,
        bool removeConflictPaths)
    {
        if (!removeChangedPaths && !removeConflictPaths)
        {
            return content;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length);
        var inChangedFileScope = false;
        var inChangedPathList = false;
        var inConflictPathList = false;
        var inConvergenceChangedFiles = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inChangedFileScope = line.Equals("## Reviewer Changed-File Scope", StringComparison.Ordinal);
                inChangedPathList = false;
                inConflictPathList = false;
                inConvergenceChangedFiles = false;
            }

            if (inChangedFileScope)
            {
                if (removeConflictPaths && line.StartsWith("Conflicting paths:", StringComparison.Ordinal))
                {
                    output.Add(ReplaceShowingCount(line));
                    inConflictPathList = true;
                    continue;
                }
                if (line.StartsWith("Staleness policy:", StringComparison.Ordinal))
                {
                    inConflictPathList = false;
                    inChangedPathList = removeChangedPaths;
                }
                if (line.StartsWith("Independent scope checks", StringComparison.Ordinal))
                {
                    inChangedPathList = false;
                }
                if (removeChangedPaths && line.StartsWith("Changed files:", StringComparison.Ordinal))
                {
                    output.Add(ReplaceShowingCount(line));
                    continue;
                }
                if ((inConflictPathList &&
                     (line.StartsWith("- conflict: ", StringComparison.Ordinal) ||
                      line.Contains("additional conflict path", StringComparison.Ordinal))) ||
                    (inChangedPathList && line.StartsWith("- ", StringComparison.Ordinal)))
                {
                    continue;
                }
            }

            if (removeChangedPaths && line.StartsWith("GOAL_DIFF_CHANGED_FILES ", StringComparison.Ordinal))
            {
                inConvergenceChangedFiles = true;
                output.Add(line);
                continue;
            }
            if (inConvergenceChangedFiles && line.StartsWith("Actively check ", StringComparison.Ordinal))
            {
                inConvergenceChangedFiles = false;
            }
            if (inConvergenceChangedFiles && line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            output.Add(line);
        }

        return string.Join(Environment.NewLine, output).Trim();
    }

    private static string ReplaceShowingCount(string line)
    {
        var separator = line.IndexOf(';');
        return separator < 0
            ? line
            : line[..separator] + "; complete path list is delivered only by its typed MandatoryFile artifact.";
    }

}
