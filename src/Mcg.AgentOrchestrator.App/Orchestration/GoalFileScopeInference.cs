using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum FileScopeProvenance
{
    Explicit,
    Derived,
    Inferred
}

internal sealed record DeclaredFileScope(string Path, FileScopeProvenance Provenance)
{
    public bool IsTrusted => Provenance is FileScopeProvenance.Explicit or FileScopeProvenance.Derived;
}

internal sealed record GoalFileScopeDerivation(
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Exclusions,
    RepositoryScopeConfidence Confidence,
    IReadOnlyList<string> Warnings)
{
    public bool Equals(GoalFileScopeDerivation? other) =>
        other is not null &&
        Confidence == other.Confidence &&
        Includes.SequenceEqual(other.Includes, StringComparer.Ordinal) &&
        Exclusions.SequenceEqual(other.Exclusions, StringComparer.Ordinal) &&
        Warnings.SequenceEqual(other.Warnings, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Confidence);
        AddValues(ref hash, Includes);
        AddValues(ref hash, Exclusions);
        AddValues(ref hash, Warnings);
        return hash.ToHashCode();
    }

    private static void AddValues(ref HashCode hash, IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }
    }
}

internal sealed class GoalFileScopeDerivationContext
{
    private readonly Lazy<IReadOnlyList<string>> _repositoryDirectories;
    private int _directoryEnumerationCount;

    public GoalFileScopeDerivationContext(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        _repositoryDirectories = new Lazy<IReadOnlyList<string>>(
            () =>
            {
                Interlocked.Increment(ref _directoryEnumerationCount);
                return SourceSurvey.EnumerateSourceDirectories(RepositoryRoot);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string RepositoryRoot { get; }

    internal IReadOnlyList<string> RepositoryDirectories => _repositoryDirectories.Value;

    internal int DirectoryEnumerationCount => Volatile.Read(ref _directoryEnumerationCount);
}

internal static class GoalFileScopeInference
{
    private static readonly Regex FileScopeRegex = new(
        @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents|\.github)[\\/][A-Za-z0-9_.*?\[\]{}\\/\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex InclusionConstraintRegex = new(
        @"(?:touch\s+only|changes?\s+must\s+be\s+confined\s+to)\s+(?<targets>.*?)(?=;|\.(?:\s|$)|\r?$|\n)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Brief-author recognition contract: docs/worker-guidance-discipline.md#declaring-and-forbidding-file-scopes
    private static readonly Regex ExclusionConstraintRegex = new(
        @"(?:(?:do\s+not|don't|must\s+not|never)\s+(?:touch|change|modify|edit|alter|rename|delete))\s+(?<targets>.*?)(?=;|\.(?:\s|$)|\r?$|\n)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TargetSeparatorRegex = new(
        @"\s*(?:,|\bor\b|\band\b)\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] KnownBoilerplateScopes =
    [
        "src/Mcg.AgentOrchestrator.App/Cli",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTests.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workers",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs",
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces",
        "scripts/Invoke-IsolatedDotnet.ps1"
    ];

    public static GoalFileScopeDerivation DeriveForIntake(string body, string repositoryRoot)
    {
        return DeriveForIntake(body, new GoalFileScopeDerivationContext(repositoryRoot));
    }

    internal static GoalFileScopeDerivation DeriveForIntake(
        string body,
        GoalFileScopeDerivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = NormalizeNewlines(body);
        var warnings = new List<string>();
        if (ContainsTraversal(text))
        {
            return new GoalFileScopeDerivation(
                [],
                [],
                RepositoryScopeConfidence.Unknown,
                ["scope text contains traversal or an out-of-repository path"]);
        }

        var exclusions = ResolveConstraints(ExclusionConstraintRegex, text, context, warnings);
        var inclusionMatches = InclusionConstraintRegex.Matches(text);
        var includes = inclusionMatches.Count > 0
            ? ResolveConstraints(InclusionConstraintRegex, text, context, warnings)
            : ResolveNamedPaths(text, context.RepositoryRoot, warnings);

        includes = Collapse(includes
            .Where(include => !exclusions.Any(exclusion => IsExcluded(include, exclusion))));
        exclusions = Collapse(exclusions);

        var confidence = includes.Count > 0 && warnings.Count == 0
            ? RepositoryScopeConfidence.Precise
            : RepositoryScopeConfidence.Unknown;
        return new GoalFileScopeDerivation(includes, exclusions, confidence, warnings);
    }

    public static bool IsKnownBoilerplateScopeSet(IReadOnlyList<string> paths) =>
        paths.Count == KnownBoilerplateScopes.Length &&
        paths.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(KnownBoilerplateScopes);

    internal static bool IsUnattributedKnownBoilerplateScopeSet(
        string body,
        IReadOnlyList<string> paths)
    {
        if (!IsKnownBoilerplateScopeSet(paths))
        {
            return false;
        }

        var normalizedBody = NormalizeNewlines(body).Replace('\\', '/');
        return KnownBoilerplateScopes.All(path =>
            !normalizedBody.Contains(path, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<DeclaredFileScope> FromText(string text)
    {
        var scopes = new Dictionary<string, FileScopeProvenance>(StringComparer.OrdinalIgnoreCase);
        var inGeneratedScopeBlock = false;
        var inExclusions = false;
        var generatedProvenance = FileScopeProvenance.Inferred;
        foreach (var line in NormalizeNewlines(text).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(BacklogIntakePlanner.TargetScopeHeadingLine, StringComparison.Ordinal))
            {
                inGeneratedScopeBlock = true;
                inExclusions = false;
                generatedProvenance = FileScopeProvenance.Inferred;
                continue;
            }

            if (inGeneratedScopeBlock &&
                trimmed.Equals(BacklogIntakePlanner.PreciseScopeMarkerLine, StringComparison.Ordinal))
            {
                generatedProvenance = FileScopeProvenance.Derived;
                continue;
            }

            if (inGeneratedScopeBlock &&
                trimmed.Equals(BacklogIntakePlanner.UnknownScopeMarkerLine, StringComparison.Ordinal))
            {
                generatedProvenance = FileScopeProvenance.Inferred;
                continue;
            }

            if (inGeneratedScopeBlock && trimmed.Equals(BacklogIntakePlanner.ScopeIncludesHeadingLine, StringComparison.Ordinal))
            {
                inExclusions = false;
                continue;
            }

            if (inGeneratedScopeBlock && trimmed.Equals(BacklogIntakePlanner.ScopeExclusionsHeadingLine, StringComparison.Ordinal))
            {
                inExclusions = true;
                continue;
            }

            var provenance = inGeneratedScopeBlock && trimmed.StartsWith("- ", StringComparison.Ordinal)
                ? generatedProvenance
                : FileScopeProvenance.Explicit;
            if (inGeneratedScopeBlock &&
                provenance == FileScopeProvenance.Explicit &&
                trimmed.Length > 0)
            {
                inGeneratedScopeBlock = false;
                inExclusions = false;
            }

            if (inExclusions)
            {
                continue;
            }

            foreach (Match match in FileScopeRegex.Matches(line))
            {
                if (!inGeneratedScopeBlock && IsInsideExclusionConstraint(line, match))
                {
                    continue;
                }

                var path = NormalizePath(match.Value);
                if (path.Length == 0)
                {
                    continue;
                }

                if (!scopes.TryGetValue(path, out var existing) ||
                    TrustRank(provenance) > TrustRank(existing))
                {
                    scopes[path] = provenance;
                }
            }
        }

        return PruneRedundantAncestors(
            scopes.Select(pair => new DeclaredFileScope(pair.Key, pair.Value)));
    }

    public static IReadOnlyList<DeclaredFileScope> FromGoal(
        Goal goal,
        int maximumCharacters,
        out bool truncated)
    {
        var taskParts = goal.Tasks.SelectMany(task =>
            string.IsNullOrWhiteSpace(task.VerificationPlan)
                ? new[] { task.Description }
                : new[] { task.Description, task.VerificationPlan! });
        var taskScopes = FromTextParts(taskParts, maximumCharacters, out var taskTruncated);
        var trustedTaskScopes = taskScopes.Where(scope => scope.IsTrusted).ToArray();
        if (trustedTaskScopes.Length > 0)
        {
            truncated = taskTruncated;
            return trustedTaskScopes;
        }

        if (HasUnknownScopeMarker(goal.Objective))
        {
            truncated = taskTruncated || goal.Objective.Length > maximumCharacters;
            return [];
        }

        var objectiveScopes = FromTextParts([goal.Objective], maximumCharacters, out var objectiveTruncated);
        var derivedObjectiveScopes = objectiveScopes
            .Where(scope => scope.Provenance == FileScopeProvenance.Derived)
            .ToArray();
        if (derivedObjectiveScopes.Length > 0)
        {
            truncated = taskTruncated || objectiveTruncated;
            return derivedObjectiveScopes;
        }

        var trustedObjectiveScopes = objectiveScopes.Where(scope => scope.IsTrusted).ToArray();
        truncated = taskTruncated || objectiveTruncated;
        return trustedObjectiveScopes.Length > 0
            ? trustedObjectiveScopes
            : Merge([taskScopes, objectiveScopes]);
    }

    internal static IReadOnlyList<DeclaredFileScope> FromTextParts(
        IEnumerable<string> parts,
        int maximumCharacters,
        out bool truncated)
    {
        var remaining = maximumCharacters;
        var selected = new List<IReadOnlyList<DeclaredFileScope>>();
        truncated = false;
        foreach (var part in parts)
        {
            if (remaining <= 0)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    truncated = true;
                }

                continue;
            }

            var scanText = part.Length <= remaining ? part : part[..remaining];
            truncated |= scanText.Length != part.Length;
            remaining -= scanText.Length;
            selected.Add(FromText(scanText));
        }

        return Merge(selected);
    }

    internal static GoalFileScopeDerivation ForScheduling(Goal goal, TaskSpec task)
    {
        var taskScopes = FromTextParts(
            string.IsNullOrWhiteSpace(task.VerificationPlan)
                ? [task.Description]
                : [task.Description, task.VerificationPlan!],
            64_000,
            out var taskTruncated);
        var trusted = taskScopes.Where(scope => scope.IsTrusted).Select(scope => scope.Path).ToArray();
        if (trusted.Length > 0)
        {
            return new GoalFileScopeDerivation(
                Collapse(trusted),
                [],
                taskTruncated ? RepositoryScopeConfidence.Unknown : RepositoryScopeConfidence.Precise,
                taskTruncated ? ["task scope text was truncated"] : []);
        }

        if (HasUnknownScopeMarker(goal.Objective))
        {
            return new GoalFileScopeDerivation(
                [],
                [],
                RepositoryScopeConfidence.Unknown,
                ["refined repository scope is explicitly unknown"]);
        }

        var goalScopes = FromText(goal.Objective);
        var derivedGoalScopes = goalScopes
            .Where(scope => scope.Provenance == FileScopeProvenance.Derived)
            .Select(scope => scope.Path)
            .ToArray();
        if (derivedGoalScopes.Length > 0)
        {
            return new GoalFileScopeDerivation(
                Collapse(derivedGoalScopes),
                [],
                RepositoryScopeConfidence.Precise,
                []);
        }

        trusted = goalScopes.Where(scope => scope.IsTrusted).Select(scope => scope.Path).ToArray();
        return new GoalFileScopeDerivation(
            Collapse(trusted),
            [],
            trusted.Length == 0 ? RepositoryScopeConfidence.Unknown : RepositoryScopeConfidence.Precise,
            trusted.Length == 0 ? ["no trusted repository scope"] : []);
    }

    private static List<string> ResolveConstraints(
        Regex regex,
        string text,
        GoalFileScopeDerivationContext context,
        ICollection<string> warnings)
    {
        var resolved = new List<string>();
        foreach (Match match in regex.Matches(text))
        {
            foreach (var target in TargetSeparatorRegex.Split(match.Groups["targets"].Value))
            {
                if (TryResolveTarget(target, context, explicitConstraint: true, out var path, out var warning))
                {
                    resolved.Add(path);
                }
                else if (!string.IsNullOrWhiteSpace(target))
                {
                    warnings.Add(warning ?? $"scope target could not be resolved: {target.Trim()}");
                }
            }
        }

        return resolved;
    }

    private static List<string> ResolveNamedPaths(
        string text,
        string repositoryRoot,
        ICollection<string> warnings)
    {
        var paths = new List<string>();
        foreach (Match match in FileScopeRegex.Matches(text))
        {
            var candidate = NormalizePath(match.Value);
            if (!IsSafeRepositoryPath(candidate))
            {
                warnings.Add($"scope path is outside the repository: {candidate}");
                continue;
            }

            var fullPath = Path.Combine(repositoryRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
            var isDeclaredNew = IsDeclaredNew(text, match.Index);
            if (File.Exists(fullPath) || Directory.Exists(fullPath) || ContainsGlob(candidate) || isDeclaredNew)
            {
                paths.Add(candidate);
            }
            else
            {
                warnings.Add($"scope path does not exist and is not declared new: {candidate}");
            }
        }

        return paths;
    }

    private static bool TryResolveTarget(
        string target,
        GoalFileScopeDerivationContext context,
        bool explicitConstraint,
        out string path,
        out string? warning)
    {
        warning = null;
        var cleaned = target.Trim().Trim('`', '"', '\'', '(', ')', '[', ']');
        cleaned = Regex.Replace(cleaned, @"^(?:the\s+)", string.Empty, RegexOptions.IgnoreCase);
        if (FileScopeRegex.Match(cleaned) is { Success: true } pathMatch)
        {
            path = NormalizePath(pathMatch.Value);
            if (!IsSafeRepositoryPath(path))
            {
                path = string.Empty;
                return false;
            }

            var fullPath = Path.Combine(context.RepositoryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            return explicitConstraint || File.Exists(fullPath) || Directory.Exists(fullPath) || ContainsGlob(path);
        }

        var segments = cleaned.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            path = string.Empty;
            return false;
        }

        var candidates = context.RepositoryDirectories
            .Where(directory => DirectorySuffixMatches(directory, segments))
            .OrderBy(candidate => candidate.Count(ch => ch == '/'))
            .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 1)
        {
            path = candidates[0];
            return true;
        }

        if (candidates.Length > 1)
        {
            warning = $"scope target is ambiguous: {target.Trim()} matches {candidates.Length} repository directories";
        }

        path = string.Empty;
        return false;
    }

    private static bool DirectorySuffixMatches(string relativePath, IReadOnlyList<string> requestedSegments)
    {
        var actual = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (actual.Length < requestedSegments.Count)
        {
            return false;
        }

        for (var index = 1; index <= requestedSegments.Count; index++)
        {
            var actualSegment = actual[^index];
            var requested = requestedSegments[^index];
            if (!actualSegment.Equals(requested, StringComparison.OrdinalIgnoreCase) &&
                !actualSegment.EndsWith("." + requested, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDeclaredNew(string text, int matchIndex)
    {
        var start = Math.Max(0, matchIndex - 80);
        var prefix = text[start..matchIndex];
        return Regex.IsMatch(
            prefix,
            @"\b(?:add|create|introduce|implement|update|modify|change|new)\b[^.\r\n]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool ContainsTraversal(string text) =>
        Regex.IsMatch(
            text,
            @"(?:^|[\s`""'/\\])\.\.(?:[\\/]|$)",
            RegexOptions.CultureInvariant);

    private static bool IsSafeRepositoryPath(string path) =>
        path.Length > 0 &&
        !Path.IsPathRooted(path) &&
        !path.Contains(':', StringComparison.Ordinal) &&
        !path.Contains('…') &&
        path.Split('/').All(segment => segment is not "." and not ".." and not "...") &&
        path.StartsWith(
            path.Split('/')[0] + "/",
            StringComparison.Ordinal);

    private static bool IsInsideExclusionConstraint(string line, Match pathMatch) =>
        ExclusionConstraintRegex.Matches(line)
            .Any(exclusion =>
                pathMatch.Index >= exclusion.Index &&
                pathMatch.Index < exclusion.Index + exclusion.Length);

    private static bool HasUnknownScopeMarker(string text) =>
        NormalizeNewlines(text).Contains(
            BacklogIntakePlanner.TargetScopeHeadingLine +
            "\n" +
            BacklogIntakePlanner.UnknownScopeMarkerLine,
            StringComparison.Ordinal);

    private static bool ContainsGlob(string path) =>
        path.IndexOfAny(['*', '?', '[', ']', '{', '}']) >= 0;

    private static bool IsExcluded(string include, string exclusion) =>
        include.Equals(exclusion, StringComparison.OrdinalIgnoreCase) ||
        include.StartsWith(exclusion.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static List<string> Collapse(IEnumerable<string> paths)
    {
        var ordered = paths
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(ch => ch == '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var collapsed = new List<string>();
        foreach (var path in ordered)
        {
            if (!collapsed.Any(parent =>
                path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
            {
                collapsed.Add(path);
            }
        }

        return collapsed;
    }

    private static IReadOnlyList<DeclaredFileScope> Merge(IEnumerable<IReadOnlyList<DeclaredFileScope>> groups)
    {
        var scopes = new Dictionary<string, FileScopeProvenance>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in groups.SelectMany(group => group))
        {
            if (!scopes.TryGetValue(scope.Path, out var existing) ||
                TrustRank(scope.Provenance) > TrustRank(existing))
            {
                scopes[scope.Path] = scope.Provenance;
            }
        }

        return PruneRedundantAncestors(
            scopes.Select(pair => new DeclaredFileScope(pair.Key, pair.Value)));
    }

    private static IReadOnlyList<DeclaredFileScope> PruneRedundantAncestors(
        IEnumerable<DeclaredFileScope> scopes)
    {
        var ordered = scopes
            .OrderBy(scope => scope.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ordered
            .Where(candidate => !ordered.Any(other =>
                other.Path.Length > candidate.Path.Length &&
                TrustRank(other.Provenance) >= TrustRank(candidate.Provenance) &&
                RepositoryPathOverlap.Classify(candidate.Path, other.Path) == PathOverlapKind.DirectoryPrefix))
            .ToArray();
    }

    private static int TrustRank(FileScopeProvenance provenance) => provenance switch
    {
        FileScopeProvenance.Explicit => 3,
        FileScopeProvenance.Derived => 2,
        _ => 1
    };

    private static string NormalizeNewlines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimEnd('.', ',', ';', ':', ')', ']');
}
