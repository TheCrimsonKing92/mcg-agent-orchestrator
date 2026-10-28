using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Derived planning facts only: these findings never grant configuration approval or completion evidence.
internal static class BriefPlannedTestLaneReadiness
{
    internal const string PendingKind = "test-lane-placement-pending";
    internal const string ConfigurationKind = "test-lane-configuration";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Identifier = @"[A-Za-z_]\w*(?:[.+][A-Za-z_]\w*)*";
    private const string NewClass = @"\bnew\s+(?:test\s+class|class)\s+[`'""]?(?<name>" + Identifier + @")\b";
    private const string TestSourcePath = @"(?<![\w./\\])tests[/\\][\w./\\-]+\.cs\b";
    private sealed record Declaration(string Name, string? Collection);

    internal static IReadOnlyList<GoalReadinessFinding> Evaluate(
        Goal goal, string repositoryRoot, string? projectHomeDirectory)
    {
        if (!goal.Tasks.Any(task => task.RequiredRole == AgentRole.Developer)) return [];
        var lines = PlanningLines(goal.Objective)
            .Concat(goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer)
                .SelectMany(task => PlanningLines(task.Description))).Distinct(StringComparer.Ordinal).ToArray();
        var manifestPath = AcceptanceManifestLocator.Resolve(repositoryRoot, projectHomeDirectory);
        try
        {
            var declarations = Declare(lines, repositoryRoot);
            // Ordinary manifest-optional readiness stays optional. An explicit test proposal needs real lane inputs.
            if (declarations.Count == 0 && projectHomeDirectory is null && !File.Exists(manifestPath)) return [];
            if (!File.Exists(manifestPath))
                throw new MissingAcceptanceManifestException(
                    AcceptanceManifestLocator.SearchedLocations(repositoryRoot, projectHomeDirectory));
            var lanes = AcceptanceGateEngineSettings.Load(repositoryRoot, projectHomeDirectory).InfrastructureTestLanes;
            var descriptors = declarations.Select(item => new AcceptanceTestClassDescriptor(item.Name, item.Collection)).ToArray();
            lanes = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
            // Validate filters even with no declarations; an empty extraction is not configuration evidence.
            _ = AcceptanceLaneMembership.LanesIncluding(lanes, "ReadinessConfigurationProbe");
            var findings = new List<GoalReadinessFinding>();
            foreach (var declaration in declarations)
            foreach (var (lane, patterns) in LaneSubstringCollisionCheck.SubstringSelections(lanes,
                         new AcceptanceTestClassDescriptor(declaration.Name, declaration.Collection)))
            {
                if (patterns.Count == 0) continue; // No selecting substring to report.
                var pending = lines.Any(line => HasPlacementPlan(line, declaration.Name, lane));
                var selectors = string.Join(", ", patterns.Select(pattern => $"FullyQualifiedName~{pattern.Trim('*')}"));
                findings.Add(new GoalReadinessFinding(
                    pending ? GoalReadinessSeverity.Info : GoalReadinessSeverity.Blocker,
                    pending ? PendingKind : LaneSubstringCollisionCheck.Rule,
                    $"New test class '{declaration.Name}' is selected by lane '{lane.Name}' through {selectors}. " +
                    (pending
                        ? "Pending planned placement: manifest/filter and pinned membership test work must still be implemented, owner-approved where required, and validated by completion and acceptance gates."
                        : "Rename the class to avoid accidental selection, or explicitly plan consistent owned placement naming this lane and its manifest/filter and pinned membership test work."),
                    CanOverride: false));
            }
            return findings;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
                                          JsonException or InvalidOperationException or ArgumentException)
        {
            return [new GoalReadinessFinding(GoalReadinessSeverity.Blocker, ConfigurationKind,
                $"Cannot evaluate planned test lanes using '{manifestPath}': {exception.Message}", CanOverride: false)];
        }
    }

    private static IReadOnlyList<Declaration> Declare(IReadOnlyList<string> lines, string root)
    {
        var result = new Dictionary<string, Declaration>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var declarations = Regex.Matches(line, NewClass, Options).Cast<Match>()
                .Concat(Regex.Matches(line, TestSourcePath, RegexOptions.CultureInvariant).Cast<Match>())
                .OrderBy(match => match.Index).ToArray();
            foreach (Match match in Regex.Matches(line, TestSourcePath, RegexOptions.CultureInvariant))
            {
                if (!IsDeclarationContext(line, match.Index) ||
                    BriefScopeSectionSelector.IsEvidenceOnly(line, match.Index, match.Length) ||
                    !IsNew(line, match.Index, match.Length)) continue;
                var path = match.Value.Replace('\\', '/');
                if (path.Split('/').Any(part => part is "." or ".." or "bin" or "obj") ||
                    File.Exists(Path.Combine(root, path))) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (Regex.IsMatch(name, @"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant))
                    result.TryAdd(name, new Declaration(name, CollectionFor(match)));
            }
            foreach (Match match in Regex.Matches(line, NewClass, Options))
            {
                var name = match.Groups["name"].Value;
                if (IsDeclarationContext(line, match.Index) &&
                    (match.Value.Contains("test class", StringComparison.OrdinalIgnoreCase) || name.Contains("Tests", StringComparison.Ordinal)) &&
                    !ExistingClass(root, name)) result.TryAdd(name, new Declaration(name, CollectionFor(match)));
            }

            string? CollectionFor(Match declaration)
            {
                var end = declarations.FirstOrDefault(next => next.Index > declaration.Index)?.Index ?? line.Length;
                // Ownership belongs to this declaration, not to another class or sentence on the same line.
                var tail = Regex.Split(line[(declaration.Index + declaration.Length)..end], @"[.!?;]\s+", Options)[0];
                var collection = Regex.Match(tail,
                    "\\bcollection\\s*(?:\\(\\s*|[:=]\\s*)?[`'\"](?<name>[^`'\"]+)[`'\"]", Options);
                return collection.Success ? collection.Groups["name"].Value : null;
            }
        }
        return result.Values.ToArray();
    }

    private static bool IsDeclarationContext(string line, int index)
    {
        // A quoted command argument is not an instruction to introduce the named class.
        if (Regex.Matches(line, @"(?<!\w)(?:""[^""]*""|'[^']*')", Options).Cast<Match>()
            .Any(quote => quote.Index < index && index < quote.Index + quote.Length &&
                !Regex.IsMatch(quote.Value[1..^1], "^" + TestSourcePath + "$", RegexOptions.CultureInvariant))) return false;
        var prefix = line[..index];
        // Evidence/example labels and command instructions retain their meaning across clauses.
        if (Regex.IsMatch(prefix,
                @"^\s*(?:for\s+)?(?:example|illustrative|historical|history|previous|regex|command|evidence|run|execute|invoke)\b", Options))
            return false;
        // Only the introducing clause declares new work. A preceding purpose clause may describe
        // existing behavior, while arbitrary prose or executable arguments do not declare anything.
        var clauses = Regex.Split(prefix, @"[,;.!?]\s+", Options);
        // Command syntax retains its argument context across punctuation, including shell wrappers.
        // A hyphenated prose word alone is not command evidence: require an argument-shaped
        // clause (one bare argument, or explicit switches/quoting) for an otherwise unknown command.
        if (Regex.IsMatch(clauses[0],
                @"^\s*(?:echo\b|cmd(?:\.exe)?\s+/(?:c|k)(?=\s|$)|[\w./\\-]*[./\\][\w./\\-]*(?=\s|$))", Options) ||
            Regex.IsMatch(clauses[0],
                @"^\s*[\w-]+-[\w-]+\s+(?:\S+\s*$|(?:.*\s)?(?:--?\w|/[A-Za-z]|[""']))", Options))
            return false;
        var context = clauses[^1];
        context = Regex.Replace(context, @"\bnew\s+(?:(?:test|source|class|file)\s+)*[`'""]?$", "", Options).Trim();
        return Regex.IsMatch(context, @"^(?:(?:add|create|introduce|implement)(?:\s+a)?\s*)?[`'""]?$", Options);
    }

    private static bool IsNew(string line, int index, int length) =>
        Regex.IsMatch(line[..index], @"\bnew\s+(?:(?:test|source|class|file)\s+)*[`'""]?$", Options) ||
        Regex.IsMatch(line[(index + length)..], @"^[`'""]?\s*\(new(?:\s+(?:test|source|class|file))*\)", Options);

    private static bool ExistingClass(string root, string name)
    {
        var tests = Path.Combine(root, "tests");
        return Directory.Exists(tests) && Directory.EnumerateFiles(tests, name.Split('.', '+').Last() + ".cs",
            SearchOption.AllDirectories).Any(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"));
    }

    private static bool HasPlacementPlan(string line, string name, AcceptanceTestLane lane)
    {
        var filterChange = Regex.Match(line, @"\b(?:change|update|add|extend|exclude|include)\b[^;.!?]*\bfilter\b", Options);
        // A negated filter action is not planned placement; unrelated declaration clauses stay intact.
        if (!filterChange.Success || Regex.IsMatch(
                Regex.Split(line[..filterChange.Index], @"[;.!?]\s+", Options).Last(),
                @"\b(?:do not|don't|must not|will not|never|not adding|no new)\b", Options)) return false;
        if (!Regex.IsMatch(line, @"\b(?:plan|planned|will|change|update|add)\b", Options) ||
            !Regex.IsMatch(line, "\\blane\\s+[`'\"]?" + Regex.Escape(lane.Name) + "[`'\"]?(?=[\\s,;:.]|$)", Options) ||
            !line.Contains("acceptance-manifest.json", StringComparison.OrdinalIgnoreCase)) return false;
        // Required test work needs its own affirmative action, independently of the filter change.
        var membershipWork = Regex.Match(line,
            @"\b(?:update|add|extend|create|implement|change)\s+(?:(?:the|a)\s+)?pinned\s+(?:lane\s+)?membership\s+test\b", Options);
        if (!membershipWork.Success || !Regex.IsMatch(
                Regex.Split(line[..membershipWork.Index], @"[;.!?]\s+|\b(?:and|but)\s+", Options).Last(),
                @"^\s*(?:(?:(?:we|this\s+change)\s+)?will\s+|(?:plan\s+to|must)\s+)?$", Options)) return false;
        // A class-specific selector is concrete planned work. Evaluate it with the canonical filter semantics.
        foreach (Match match in Regex.Matches(line, @"FullyQualifiedName(?:!~|!=|~|=)[^\s`'"",;]+",
                     RegexOptions.CultureInvariant))
        {
            var filter = match.Value;
            // Source manifests reject exact selectors, even though resolved owned-collection filters allow them.
            // Validate the complete token, including any unsupported suffix, rather than an identifier prefix.
            try { _ = AcceptanceCheckCommandBuilder.TranslateMtpFilter(filter).ToArray(); }
            catch (InvalidOperationException) { continue; }
            if (!filter.EndsWith(name, StringComparison.Ordinal) &&
                !filter.EndsWith(name.Split('.', '+').Last(), StringComparison.Ordinal)) continue;
            var planned = lane with { Filter = filter };
            var selected = AcceptanceLaneMembership.LanesIncluding([planned], name).Count > 0;
            if (filter.Contains('!') ? !selected : selected) return true;
        }
        return false;
    }

    private static IEnumerable<string> PlanningLines(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var scope = BriefScopeSectionSelector.FindScopeRegion(lines);
        var inNewTests = false;
        var fenced = false;
        var hasHeading = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = Regex.Replace(lines[index].Trim(), @"^(?:[-*+]\s+|\d+\.\s+)", "");
            if (line.StartsWith("```") || line.StartsWith("~~~")) { fenced = !fenced; continue; }
            if (fenced) continue;
            // Keep inline identifiers, paths and selectors; arbitrary command contents cannot declare a class.
            line = Regex.Replace(line, @"`([^`]*)`", match =>
                Regex.IsMatch(line[..match.Index], @"\b(?:lane|collection)\s*(?:\(\s*|[:=]\s*)?$", Options) ||
                Regex.IsMatch(match.Groups[1].Value, "^(?:" + Identifier + "|" + TestSourcePath +
                    @"|FullyQualifiedName(?:!~|~|!=|=)" + Identifier + ")$", RegexOptions.CultureInvariant)
                    ? match.Value : "");
            if (line.StartsWith('#'))
            {
                hasHeading = true;
                inNewTests = Regex.IsMatch(line.TrimStart('#').Trim(), @"^(?:new tests?|new test classes|test plan|test planning)$", Options);
                continue;
            }
            if (scope is { } range ? !(index >= range.Start && index < range.End) && !inNewTests
                : hasHeading && !inNewTests) continue;
            if (line.StartsWith('>') || line.StartsWith('"') || line.StartsWith('\'') ||
                Regex.IsMatch(line, @"^(?:[-*]\s+)?(?:dotnet|rg|git|pwsh|powershell)\b", Options)) continue;
            yield return line;
        }
    }
}
