using Mcg.AgentOrchestrator.Core;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum WorkerContextDeliveryPolicy
{
    Initial,
    RetryFeedback,
    CompactRepair
}

internal static class WorkerContextRenderer
{
    internal static string CreateHeaderResidual(TaskBriefSource source)
    {
        var instructionIndex = FindInstructionSegment(source);
        var header = string.Join(
            Environment.NewLine,
            source.Segments.Take(instructionIndex).SelectMany(segment => segment.Lines));
        header = RemoveMarkedBriefBlock(
            header,
            "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->",
            "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->");
        header = RemoveMarkedBriefBlock(
            header,
            "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_START -->",
            "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_END -->");
        header = RemoveMarkedBriefBlock(
            header,
            "<!-- ACCEPTANCE_FAILURE_START -->",
            "<!-- ACCEPTANCE_FAILURE_END -->");
        header = RemoveLineRange(header, "Goal: ", "Goal id: ");
        header = RemoveLineRange(header, "Task: ", "Task role: ");
        foreach (var prefix in new[]
        {
            "# Agent Task Brief",
            "Goal id: ",
            "Goal status: ",
            "Task role: ",
            "Task status: ",
            "Task id: ",
            "Working directory, use absolute paths: ",
            "Context files: read "
        })
        {
            header = RemoveLineWithPrefix(header, prefix);
        }

        return header.Trim();
    }

    internal static string CreateCurrentBrief(
        TaskBriefSource source,
        bool removeReviewerChangedPaths = false,
        bool removeReviewerConflictPaths = false)
    {
        var instructionIndex = FindInstructionSegment(source);
        var lines = source.Segments
            .Skip(instructionIndex)
            .Where(segment => segment.TypedProjectionIdentity is null)
            .SelectMany(segment => segment.Lines);
        var current = string.Join(Environment.NewLine, lines).Trim();
        return source.Role == AgentRole.Reviewer
            ? ApplyReviewerPreviewPolicy(
                current,
                removeReviewerChangedPaths,
                removeReviewerConflictPaths)
            : current;
    }

    internal static string Render(
        TaskBriefSource source,
        WorkerContextPackage package,
        string workingDirectory,
        WorkerContextDeliveryPolicy policy)
    {
        ValidateTypedSelection(source, package, workingDirectory, policy);
        return WorkerContextPackageBuilder.Render(package);
    }

    internal static void ValidateTypedSelection(
        TaskBriefSource source,
        WorkerContextPackage package,
        string workingDirectory,
        WorkerContextDeliveryPolicy policy)
    {
        if (package.TargetRole != source.Role)
        {
            throw new InvalidOperationException(
                $"Typed worker context role '{source.Role}' does not match package role '{package.TargetRole}'.");
        }

        var artifactsByIdentity = package.Artifacts.ToDictionary(
            artifact => artifact.Identity,
            artifact => artifact);
        foreach (var segment in source.Segments)
        {
            if (segment.RoleVisibility is null || !segment.RoleVisibility.Contains(source.Role))
            {
                throw new WorkerContextPreparationException(
                    segment.TypedProjectionIdentity ?? new LogicalArtifactIdentity("brief/current.md"),
                    "typed-role-visibility-mismatch",
                    $"Typed brief segment is not visible to {source.Role}.");
            }

            if (segment.TypedProjectionIdentity is not { } identity)
            {
                continue;
            }

            if (!artifactsByIdentity.TryGetValue(identity, out var artifact))
            {
                throw new WorkerContextPreparationException(
                    identity,
                    "typed-identity-unresolved",
                    "A selected typed brief identity has no verified package artifact.");
            }

            if (!artifact.RoleVisibility.Contains(source.Role))
            {
                throw new WorkerContextPreparationException(
                    identity,
                    "typed-role-visibility-mismatch",
                    $"The verified package artifact is not visible to {source.Role}.");
            }
        }

        foreach (var artifact in package.Artifacts)
        {
            ValidateArtifactHash(artifact, workingDirectory);
        }

        if (policy is WorkerContextDeliveryPolicy.RetryFeedback or WorkerContextDeliveryPolicy.CompactRepair &&
            !artifactsByIdentity.ContainsKey(new LogicalArtifactIdentity("task/criterion-retry-feedback.json")))
        {
            throw new WorkerContextPreparationException(
                new LogicalArtifactIdentity("task/criterion-retry-feedback.json"),
                "retry-feedback-artifact-missing",
                "Retry delivery policy requires the current typed feedback artifact.");
        }
    }

    private static void ValidateArtifactHash(WorkerContextArtifact artifact, string workingDirectory)
    {
        var bytes = artifact.AuthoritativeBytes;
        if (artifact.MandatoryRelativePath is { } relativePath)
        {
            var root = Path.GetFullPath(workingDirectory);
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                throw new WorkerContextPreparationException(
                    artifact.Identity,
                    "typed-materialization-unavailable",
                    "The file-backed typed artifact is missing or outside the dispatch root.");
            }

            bytes = File.ReadAllBytes(path);
        }

        if (bytes is null ||
            !WorkerContextArtifact.Hash(bytes).Equals(artifact.ContentHash, StringComparison.Ordinal))
        {
            throw new WorkerContextPreparationException(
                artifact.Identity,
                "typed-identity-hash-mismatch",
                "The selected typed artifact content does not match its identity-bound hash.");
        }
    }

    private static int FindInstructionSegment(TaskBriefSource source)
    {
        for (var index = 0; index < source.Segments.Count; index++)
        {
            if (source.Segments[index].Lines.Any(line =>
                line.Equals("## Instructions", StringComparison.Ordinal)))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Typed context brief is missing its Instructions source boundary.");
    }

    internal static string ApplyReviewerPreviewPolicy(
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

    private static string RemoveMarkedBriefBlock(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Typed context brief block '{startMarker}' has no closing marker '{endMarker}'.");
        }

        end += endMarker.Length;
        while (end < content.Length && (content[end] == '\r' || content[end] == '\n'))
        {
            end++;
        }

        return content.Remove(start, end - start);
    }

    private static string RemoveLineRange(string content, string startPrefix, string endPrefix)
    {
        var start = FindLineWithPrefix(content, startPrefix, 0);
        if (start < 0)
        {
            return content;
        }

        var end = FindLineWithPrefix(content, endPrefix, start + startPrefix.Length);
        if (end < 0)
        {
            throw new InvalidOperationException($"Typed context brief source '{startPrefix}' has no boundary '{endPrefix}'.");
        }

        return content.Remove(start, end - start);
    }

    private static string RemoveLineWithPrefix(string content, string prefix)
    {
        var start = FindLineWithPrefix(content, prefix, 0);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf('\n', start);
        return end < 0 ? content[..start] : content.Remove(start, end - start + 1);
    }

    private static int FindLineWithPrefix(string content, string prefix, int startIndex)
    {
        var candidate = content.IndexOf(prefix, startIndex, StringComparison.Ordinal);
        while (candidate >= 0)
        {
            if (candidate == 0 || content[candidate - 1] == '\n')
            {
                return candidate;
            }

            candidate = content.IndexOf(prefix, candidate + prefix.Length, StringComparison.Ordinal);
        }

        return -1;
    }
}
