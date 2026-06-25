using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerSourceSurvey
{
    private const int SourceSurveyMaxFiles = 60;
    private const int SourceSurveyMaxAnalysisFiles = 90;
    private const int SourceSurveyMaxFileLines = 500;
    private static readonly Regex PublicTypeRegex = new(
        @"^\s*(public|internal|protected internal|protected)\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+)*(class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PublicMemberRegex = new(
        @"^\s*(public|internal|protected internal|protected)\s+(?:static\s+|async\s+|virtual\s+|override\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+)*(?!class\b|interface\b|record\b|struct\b|enum\b)[A-Za-z_][A-Za-z0-9_<>,\[\].?\s]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*(\(|\{|=>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal string BuildSourceSurvey(Goal goal, TaskSpec task, string workingDirectory)
    {
        var sourceFiles = EnumerateSourceFiles(workingDirectory).ToList();
        var terms = BuildSearchTerms(goal, task);
        var matches = sourceFiles
            .Where(path => terms.Any(term => path.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Take(SourceSurveyMaxFiles)
            .ToList();
        var publicSymbols = BuildPublicApiSymbols(workingDirectory, sourceFiles);
        var likelyTests = BuildLikelyTests(sourceFiles, terms);
        var callSiteHints = BuildCallSiteHints(workingDirectory, sourceFiles, terms);
        var ownershipHints = BuildOwnershipHints(workingDirectory, sourceFiles);
        var groups = sourceFiles
            .GroupBy(GetSurveyDirectory)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var sample = sourceFiles
            .Take(SourceSurveyMaxFiles)
            .ToList();
        var lines = new List<string>
        {
            "# Source Survey",
            string.Empty,
            $"Working directory: {workingDirectory}",
            $"Source files indexed: {sourceFiles.Count}",
            $"Returned file limit: {SourceSurveyMaxFiles}",
            "Regeneration: generated at dispatch preparation; treat as stale when source files, git status, objective, task text, or verification plan changes after dispatch.",
            string.Empty,
            "## Directory Counts"
        };

        if (groups.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            lines.AddRange(groups.Select(group => $"- {group.Key}: {group.Count()}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Task-Term Matches");
        if (matches.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            lines.AddRange(matches.Select(path => $"- {path}"));
        }

        lines.Add(string.Empty);
        lines.Add("## Source Sample");
        if (sample.Count == 0)
        {
            lines.Add("- none");
        }
        else
        {
            lines.AddRange(sample.Select(path => $"- {path}"));
        }

        AddSurveySection(lines, "Likely Tests", likelyTests);
        AddSurveySection(lines, "Public API Symbols", publicSymbols);
        AddSurveySection(lines, "Call-Site Hints", callSiteHints);
        AddSurveySection(lines, "Ownership Hints", ownershipHints);

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddSurveySection(List<string> lines, string heading, List<string> values)
    {
        lines.Add(string.Empty);
        lines.Add($"## {heading}");
        if (values.Count == 0)
        {
            lines.Add("- none");
            return;
        }

        lines.AddRange(values.Select(value => $"- {value}"));
    }

    internal List<string> BuildPublicApiSymbols(string workingDirectory, IReadOnlyList<string> sourceFiles)
    {
        var symbols = new List<string>();
        foreach (var relativePath in sourceFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Take(SourceSurveyMaxAnalysisFiles))
        {
            foreach (var line in ReadSourceLines(workingDirectory, relativePath))
            {
                var typeMatch = PublicTypeRegex.Match(line);
                if (typeMatch.Success)
                {
                    symbols.Add($"{relativePath}: {typeMatch.Groups[1].Value} {typeMatch.Groups[2].Value} {typeMatch.Groups[3].Value}");
                    break;
                }

                var memberMatch = PublicMemberRegex.Match(line);
                if (memberMatch.Success)
                {
                    symbols.Add($"{relativePath}: {memberMatch.Groups[1].Value} member {memberMatch.Groups[2].Value}");
                    break;
                }
            }

            if (symbols.Count >= SourceSurveyMaxFiles)
            {
                break;
            }
        }

        return symbols;
    }

    internal List<string> BuildLikelyTests(IReadOnlyList<string> sourceFiles, IReadOnlyList<string> terms)
    {
        return sourceFiles
            .Where(IsLikelyTestPath)
            .OrderByDescending(path => terms.Any(term => path.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(SourceSurveyMaxFiles)
            .ToList();
    }

    internal List<string> BuildCallSiteHints(string workingDirectory, IReadOnlyList<string> sourceFiles, List<string> terms)
    {
        if (terms.Count == 0)
        {
            return [];
        }

        var hints = new List<string>();
        foreach (var relativePath in sourceFiles.Take(SourceSurveyMaxAnalysisFiles))
        {
            var matchedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in ReadSourceLines(workingDirectory, relativePath))
            {
                foreach (var term in terms)
                {
                    if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedTerms.Add(term);
                    }
                }

                if (matchedTerms.Count >= 4)
                {
                    break;
                }
            }

            if (matchedTerms.Count > 0)
            {
                hints.Add($"{relativePath}: {string.Join(", ", matchedTerms.OrderBy(term => term, StringComparer.OrdinalIgnoreCase))}");
            }

            if (hints.Count >= SourceSurveyMaxFiles)
            {
                break;
            }
        }

        return hints;
    }

    internal List<string> BuildOwnershipHints(string workingDirectory, IReadOnlyList<string> sourceFiles)
    {
        var hints = sourceFiles
            .GroupBy(GetOwnershipBucket, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(group => $"{group.Key}: {group.Count()} file(s)")
            .ToList();

        if (File.Exists(Path.Combine(workingDirectory, "AGENTS.md")))
        {
            hints.Insert(0, "AGENTS.md: repository-local operator and worker instructions");
        }

        return hints;
    }

    private static IEnumerable<string> ReadSourceLines(string workingDirectory, string relativePath)
    {
        var path = Path.Combine(workingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            yield break;
        }

        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            yield return line;
            count++;
            if (count >= SourceSurveyMaxFileLines)
            {
                yield break;
            }
        }
    }

    private static bool IsLikelyTestPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(normalized);
        return normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(".Tests/", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOwnershipBucket(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}: production source" : "src: production source";
        }

        if (normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}: test source" : "tests: test source";
        }

        if (normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
        {
            return "scripts: operator tooling";
        }

        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
        {
            return "docs: repository documentation";
        }

        if (normalized.StartsWith(".agents/", StringComparison.OrdinalIgnoreCase))
        {
            return ".agents: worker skills and guidance";
        }

        return ".: repository root";
    }

    private static IEnumerable<string> EnumerateSourceFiles(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return [];
        }

        var toolchain = TargetToolchainDetector.Detect(workingDirectory);
        var extensions = TargetToolchainDetector.GetSourceExtensions(toolchain);

        return EnumerateFilesPruned(workingDirectory, workingDirectory)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesPruned(string root, string directory)
    {
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            var relativeDirectory = Path.GetRelativePath(root, childDirectory).Replace('\\', '/');
            if (IsExcludedSurveyPath(relativeDirectory))
            {
                continue;
            }

            foreach (var childFile in EnumerateFilesPruned(root, childDirectory))
            {
                yield return childFile;
            }
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!IsExcludedSurveyPath(relativePath))
            {
                yield return relativePath;
            }
        }
    }

    private static bool IsExcludedSurveyPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".scratch", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".orchestrator-context", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".orchestrator-prototype", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".orchestrator-worktrees", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("playwright-report", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetSurveyDirectory(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "." : normalized[..slash];
    }

    internal List<string> BuildSearchTerms(Goal goal, TaskSpec task)
    {
        var text = $"{goal.Objective} {task.Description} {task.VerificationPlan}";
        return text
            .Split([' ', '\t', '\r', '\n', '.', ',', ':', ';', '/', '\\', '-', '_', '`', '\'', '"', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length >= 4)
            .Select(term => term.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }
}
