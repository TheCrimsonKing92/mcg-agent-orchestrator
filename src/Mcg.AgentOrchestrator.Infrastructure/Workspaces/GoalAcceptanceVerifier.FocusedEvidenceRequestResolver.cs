using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceFilter = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilter;
using FocusedEvidencePlannedCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidencePlannedCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class FocusedEvidenceRequestResolver
{
    internal static bool TryBuildFocusedEvidenceChecks(
        string request,
        AcceptanceGateEngineSettings engineSettings,
        string worktreePath,
        out IReadOnlyList<AcceptanceManifestCheck> checks,
        out FocusedEvidenceCoverage coverage,
        out FocusedEvidenceRejection rejection)
    {
        checks = [];
        coverage = new FocusedEvidenceCoverage(TargetToChecks: []);
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.EmptyRequest,
            request,
            "empty evidence request");
        var items = request
            .Split(';')
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        if (items.Length == 0)
        {
            return false;
        }

        var validated = new List<(string Target, string Project, FocusedEvidenceFilter? Filter)>();
        var totalTargets = 0;
        foreach (var rawItem in items)
        {
            var item = rawItem.Trim();
            var project = InfrastructureTestsProject;
            var expression = rawItem;
            var hasExplicitProject = false;
            var separator = rawItem.IndexOf(':', StringComparison.Ordinal);
            if (separator >= 0)
            {
                hasExplicitProject = true;
                var alias = rawItem[..separator].Trim();
                expression = rawItem[(separator + 1)..];
                if (!TryResolveFocusedEvidenceProject(alias, engineSettings, out project))
                {
                    rejection = new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnsupportedProject,
                        alias,
                        $"unsupported evidence request project alias '{alias}'");
                    return false;
                }
            }

            FocusedEvidenceFilter? filter;
            int targetCount;
            try
            {
                if (!TryNormalizeFocusedEvidenceFilter(
                        expression,
                        allowMappedProject: hasExplicitProject,
                        worktreePath,
                        project,
                        out filter,
                        out targetCount,
                        out rejection))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rejection = BuildFocusedEvidenceSourceDiscoveryRejection(expression, ex);
                return false;
            }

            totalTargets += targetCount;
            if (filter is not null && ProjectMatches(project, InfrastructureTestsProject))
            {
                IReadOnlyList<(string Project, FocusedEvidenceFilter Filter)> projectArms;
                try
                {
                    if (!TryExpandExtractedFocusedEvidenceProjects(
                            worktreePath,
                            engineSettings,
                            project,
                            filter,
                            out projectArms,
                            out rejection))
                    {
                        return false;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    rejection = BuildFocusedEvidenceSourceDiscoveryRejection(filter.OriginalToken, ex);
                    return false;
                }

                validated.AddRange(projectArms.Select(arm => (item, arm.Project, arm.Filter)));
            }
            else
            {
                validated.Add((item, project, filter));
            }
        }

        // Historical rule: more than four focused targets collapsed to one whole-project check per
        // project (and 18 Infrastructure lanes). It optimized process/check count and replaced the
        // earlier hard rejection of mappings over four targets; no filter-length or runner failure
        // was recorded. Keep every validated filter, while retaining the old path's manifest-default
        // timeout for wide mappings so serial focused execution does not lose its completion budget.
        // Performance prediction pending the operator-owned like-for-like run: one build phase is
        // created per arm, but each request item starts a serial MTP process with fixed cost F~=9-11s.
        // Thus the six-target Infrastructure shape is B+R6+kF: 9-11s of process cost when packed into
        // one item, or 54-66s when split across k=6 items, versus the 1775.9s/3367-test collapsed
        // baseline. Fixed process cost alone crosses 1775.9s at k=162 (F=11), 178 (F=10), or 198
        // (F=9); build plus filtered runtime B+R6 moves that crossing earlier. The two-target,
        // cross-project shape remains B+R2+2F (18-22s of process cost), so predict its existing
        // 231.6s/220-test focused result within ordinary run variance. RunCheckBatchAsync only shards
        // named Infrastructure acceptance lanes, so mapped focused items do not recover the former
        // four-lane concurrency. The operator receipt must replace these predictions with measured
        // wall-clock and tests_executed for both exact target sets.
        var planned = new List<FocusedEvidencePlannedCheck>();
        var batchedCompatibleItems = false;
        var unbatchedReasons = new List<string>();
        foreach (var projectGroup in validated.GroupBy(item => item.Project, StringComparer.OrdinalIgnoreCase))
        {
            var projectUnbatchedReasons = new HashSet<string>(StringComparer.Ordinal);
            var compatible = projectGroup
                .Where(item => item.Filter is not null && IsPositiveFocusedEvidenceDisjunction(item.Filter))
                .ToArray();
            if (compatible.Length > 1)
            {
                var tokens = compatible
                    .SelectMany(item => item.Filter!.Tokens)
                    .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
                    .OrderBy(token => token.CanonicalToken, StringComparer.Ordinal)
                    .ToArray();
                var canonical = string.Join("|", tokens.Select(token => token.CanonicalToken));
                if (canonical.Length <= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)
                {
                    planned.Add(new FocusedEvidencePlannedCheck(
                        compatible.Select(item => item.Target).Distinct(StringComparer.Ordinal).ToArray(),
                        projectGroup.Key,
                        new FocusedEvidenceFilter(
                            string.Join("; ", compatible.Select(item => item.Target)),
                            canonical,
                            tokens),
                        compatible.Select(item => item.Filter!).ToArray()));
                    batchedCompatibleItems = true;
                }
                else
                {
                    var group = new List<(string Target, string Project, FocusedEvidenceFilter? Filter)>();
                    var packedGroupCount = 0;
                    foreach (var item in compatible
                        .OrderBy(item => item.Filter!.CanonicalText, StringComparer.Ordinal)
                        .ThenBy(item => item.Target, StringComparer.Ordinal))
                    {
                        var candidateTokens = group.Append(item)
                            .SelectMany(member => member.Filter!.Tokens)
                            .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
                            .OrderBy(token => token.CanonicalToken, StringComparer.Ordinal)
                            .ToArray();
                        var candidateCanonical = string.Join("|", candidateTokens.Select(token => token.CanonicalToken));
                        if (group.Count > 0 &&
                            (candidateCanonical.Length > GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength ||
                             item.Filter!.CanonicalText.Length >= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength))
                        {
                            AddPackedGroup();
                        }
                        group.Add(item);
                        // Boundary-sized items stay whole and occupy their own check.
                        if (item.Filter!.CanonicalText.Length >= GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)
                        {
                            AddPackedGroup();
                        }
                    }
                    if (group.Count > 0)
                    {
                        AddPackedGroup();
                    }
                    if (packedGroupCount > 1)
                    {
                        projectUnbatchedReasons.Add("bounded-filter-overflow");
                    }

                    void AddPackedGroup()
                    {
                        if (group.Count == 1)
                        {
                            var item = group[0];
                            planned.Add(new FocusedEvidencePlannedCheck(
                                [item.Target], item.Project, item.Filter, [item.Filter!]));
                        }
                        else
                        {
                            var groupTokens = group
                                .SelectMany(item => item.Filter!.Tokens)
                                .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
                                .OrderBy(token => token.CanonicalToken, StringComparer.Ordinal)
                                .ToArray();
                            planned.Add(new FocusedEvidencePlannedCheck(
                                group.Select(item => item.Target).Distinct(StringComparer.Ordinal).ToArray(),
                                projectGroup.Key,
                                new FocusedEvidenceFilter(
                                    string.Join("; ", group.Select(item => item.Target)),
                                    string.Join("|", groupTokens.Select(token => token.CanonicalToken)),
                                    groupTokens),
                                group.Select(item => item.Filter!).ToArray()));
                        }
                        packedGroupCount++;
                        group.Clear();
                    }
                }
            }
            else
            {
                planned.AddRange(compatible.Select(item => new FocusedEvidencePlannedCheck(
                    (IReadOnlyList<string>)[item.Target],
                    item.Project,
                    item.Filter,
                    [item.Filter!])));
            }

            var incompatible = projectGroup
                .Where(item => item.Filter is null || !IsPositiveFocusedEvidenceDisjunction(item.Filter))
                .ToArray();
            if (incompatible.Length > 0 && projectGroup.Count() > 1)
            {
                projectUnbatchedReasons.Add("incompatible-filter-semantics");
            }
            planned.AddRange(incompatible.Select(item => new FocusedEvidencePlannedCheck(
                (IReadOnlyList<string>)[item.Target],
                item.Project,
                item.Filter,
                item.Filter is null ? [] : [item.Filter])));
            unbatchedReasons.AddRange(projectUnbatchedReasons.Select(reason =>
                $"{GoalAcceptanceVerifier.ProjectLabel(projectGroup.Key)}={reason}"));
        }

        var budget = MeasureFocusedEvidenceBudget(worktreePath, validated, totalTargets);
        var built = new List<AcceptanceManifestCheck>();
        foreach (var item in planned)
        {
            var check = new AcceptanceManifestCheck
            {
                Name = item.Filter is null
                    ? $"reviewer mapped project evidence: {GoalAcceptanceVerifier.ProjectLabel(item.Project)}"
                    : $"reviewer focused evidence: {GoalAcceptanceVerifier.ProjectLabel(item.Project)} {item.Filter.CanonicalText}",
                Type = "dotnet-test",
                // Focused-evidence checks are synthesized from a referenced project, so runner selection
                // follows that project's declaration just like policy and impact-plan checks.
                Runner = GoalAcceptanceVerifier.ResolveDotnetTestRunner(worktreePath, item.Project),
                Project = item.Project,
                Arguments = item.Filter is null
                    ? ["--verbosity", "minimal"]
                    : ["--verbosity", "minimal", "--filter", item.Filter.CanonicalText],
                FocusedEvidenceTokens = item.Filter?.Tokens ?? [],
                FocusedEvidenceSelections = item.SelectionFilters
                    .Select(filter => filter.Tokens)
                    .ToArray(),
                IsFocusedEvidenceSelection = item.Filter is not null,
                FocusedEvidenceMatchedClasses = budget.For(item.Project, item.SelectionFilters),
                TimeoutMinutes = budget.TargetCount <= GoalAcceptanceVerifier.FocusedEvidenceShortTimeoutTargetLimit ? 10 : null
            };
            built.Add(check);
        }

        var targetToChecks = planned
            .SelectMany((item, index) => item.Targets.Select(target => (Target: target, CheckName: built[index].Name)))
            .GroupBy(item => item.Target, StringComparer.Ordinal)
            .Select(group => new FocusedEvidenceTargetCoverage(
                group.Key,
                group.Select(item => item.CheckName).ToArray()))
            .ToArray();

        var hasFocusedFilters = planned.Any(item => item.Filter is not null);
        var hasMappedProjects = planned.Any(item => item.Filter is null);
        var executionReason = hasFocusedFilters && hasMappedProjects
            ? "explicit-focused-and-mapped-project-request"
            : hasFocusedFilters ? "explicit-focused-mapping" : "explicit-mapped-project-request";
        if (batchedCompatibleItems)
        {
            executionReason += "+compatible-same-project-batch";
        }
        if (unbatchedReasons.Count > 0)
        {
            executionReason += $"+unbatched:{string.Join(',', unbatchedReasons.OrderBy(reason => reason, StringComparer.Ordinal))}";
        }
        checks = built;
        coverage = new FocusedEvidenceCoverage(
            TargetToChecks: targetToChecks,
            ExecutionMode: hasFocusedFilters && hasMappedProjects
                ? "mixed"
                : hasFocusedFilters ? "focused" : "project",
            ExecutionReason: executionReason);
        return true;
    }

    private static bool IsPositiveFocusedEvidenceDisjunction(FocusedEvidenceFilter filter) =>
        filter.Tokens.Count > 0 &&
        filter.Tokens.All(token => token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method) &&
        !filter.CanonicalText.Contains('&', StringComparison.Ordinal);

    private static string? ResolveExtractedFocusedEvidenceProject(
        string worktreePath,
        AcceptanceGateEngineSettings engineSettings,
        FocusedEvidenceFilter filter)
    {
        var classNames = filter.Tokens
            .Where(token => token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method)
            .Select(token => token.ContainingClass)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (classNames.Length == 0)
        {
            return null;
        }

        var matchingProjects = engineSettings.MtpInvocations
            .Select(invocation => NormalizePath(invocation.Project))
            .Where(project => IsExtractedInfrastructureProject(project!))
            .Where(project => classNames.All(className =>
                ProjectContainsFocusedEvidenceClass(worktreePath, project!, className)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matchingProjects.Length == 1 ? matchingProjects[0] : null;
    }

    private static bool TryExpandExtractedFocusedEvidenceProjects(
        string worktreePath,
        AcceptanceGateEngineSettings engineSettings,
        string umbrellaProject,
        FocusedEvidenceFilter filter,
        out IReadOnlyList<(string Project, FocusedEvidenceFilter Filter)> arms,
        out FocusedEvidenceRejection rejection)
    {
        arms = [];
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnresolvableSelection,
            filter.OriginalToken,
            "focused evidence selection could not be routed");
        var positiveTokens = filter.Tokens
            .Where(token => token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method)
            .ToArray();
        var isPositiveDisjunction = positiveTokens.Length == filter.Tokens.Count &&
            !filter.CanonicalText.Contains('&', StringComparison.Ordinal);
        if (!isPositiveDisjunction)
        {
            var extractedProject = ResolveExtractedFocusedEvidenceProject(
                worktreePath,
                engineSettings,
                filter);
            arms = [(extractedProject ?? umbrellaProject, filter)];
            return true;
        }

        var extractedProjects = engineSettings.MtpInvocations
            .Select(invocation => NormalizePath(invocation.Project))
            .Where(project => IsExtractedInfrastructureProject(project!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var grouped = new List<(string Project, List<string> Filters)>();
        foreach (var token in positiveTokens)
        {
            var matchingProjects = extractedProjects
                .Where(project => ProjectContainsFocusedEvidenceClass(
                    worktreePath,
                    project!,
                    token.ContainingClass))
                .ToArray();
            if (matchingProjects.Length > 1)
            {
                rejection = new FocusedEvidenceRejection(
                    FocusedEvidenceRejectionCode.UnresolvableSelection,
                    token.OriginalToken,
                    $"focused evidence class '{token.ContainingClass}' is present in multiple registered extracted projects");
                return false;
            }

            var owningProject = matchingProjects.SingleOrDefault() ?? umbrellaProject;
            var group = grouped.FirstOrDefault(candidate => candidate.Project.Equals(
                owningProject,
                StringComparison.OrdinalIgnoreCase));
            if (group.Filters is null)
            {
                group = (owningProject, []);
                grouped.Add(group);
            }
            group.Filters.Add(token.CanonicalToken);
        }

        arms = grouped
            .Select(group =>
            {
                var groupTokens = filter.Tokens
                    .Where(token => group.Filters.Contains(token.CanonicalToken, StringComparer.Ordinal))
                    .ToArray();
                return (
                    group.Project,
                    new FocusedEvidenceFilter(
                        filter.OriginalToken,
                        string.Join("|", group.Filters),
                        groupTokens));
            })
            .ToArray();
        return true;
    }

    private static bool ProjectContainsFocusedEvidenceClass(
        string worktreePath,
        string project,
        string className) =>
        FindFocusedEvidenceClassFiles(worktreePath, project, className).Count > 0;

    internal static IReadOnlyList<string> FindFocusedEvidenceClassFiles(
        string worktreePath,
        string project,
        string className)
    {
        return EnumerateFocusedEvidenceSourceFiles(worktreePath, project)
            .Where(path => ParseCSharpRoot(path)
                .DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Any(declaration => FocusedEvidenceTypeMatches(declaration, className)))
            .ToArray();
    }

    private static FocusedEvidenceRejection BuildFocusedEvidenceSourceDiscoveryRejection(
        string originalToken,
        Exception exception) =>
        new(
            FocusedEvidenceRejectionCode.SourceDiscoveryFailure,
            originalToken,
            $"focused evidence source discovery failed: {exception.GetType().Name}: {exception.Message}");

    private static CompilationUnitSyntax ParseCSharpRoot(string path) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetCompilationUnitRoot();

    private static bool FocusedEvidenceTypeMatches(TypeDeclarationSyntax declaration, string className)
    {
        var requestedName = className.Trim();
        if (declaration.Identifier.ValueText.Equals(requestedName, StringComparison.Ordinal))
        {
            return true;
        }

        var namespaceNames = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString());
        var containingTypeNames = declaration.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Identifier.ValueText);
        var qualifiedName = string.Join(
            '.',
            namespaceNames.Concat(containingTypeNames).Append(declaration.Identifier.ValueText));
        return qualifiedName.Equals(requestedName, StringComparison.Ordinal) ||
            qualifiedName.EndsWith('.' + requestedName, StringComparison.Ordinal);
    }

    internal static bool TryResolveFocusedEvidenceProject(
        string alias,
        out string project) =>
        TryResolveFocusedEvidenceProject(alias, engineSettings: null, out project);

    internal static bool TryResolveFocusedEvidenceProject(
        string alias,
        AcceptanceGateEngineSettings? engineSettings,
        out string project)
    {
        var normalized = alias.Replace('\\', '/').Trim();
        project = normalized switch
        {
            "Core.Tests" or "Core" or "Mcg.AgentOrchestrator.Core.Tests" => CoreTestsProject,
            "Infrastructure.Tests" or "Infrastructure" or "Mcg.AgentOrchestrator.Infrastructure.Tests" => InfrastructureTestsProject,
            _ when normalized.EndsWith(CoreTestsProject, StringComparison.OrdinalIgnoreCase) => CoreTestsProject,
            _ when normalized.EndsWith(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase) => InfrastructureTestsProject,
            _ => string.Empty
        };
        if (project.Length > 0)
        {
            return true;
        }

        return DeclaredTestProjectInventory.TryResolve(normalized, engineSettings, out project);
    }

    private static bool TryNormalizeFocusedEvidenceFilter(
        string expression,
        bool allowMappedProject,
        string worktreePath,
        string project,
        out FocusedEvidenceFilter? filter,
        out int targetCount,
        out FocusedEvidenceRejection rejection)
    {
        filter = null;
        targetCount = 0;
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnsupportedToken,
            expression,
            "focused evidence filter is invalid");
        var trimmed = expression.Trim();
        if (trimmed.Length == 0)
        {
            rejection = new FocusedEvidenceRejection(
                FocusedEvidenceRejectionCode.EmptyRequest,
                expression,
                "empty focused evidence filter");
            return false;
        }

        if (expression.Length > GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength)
        {
            rejection = new FocusedEvidenceRejection(
                FocusedEvidenceRejectionCode.OversizedFilter,
                expression,
                $"focused evidence filter exceeds the {GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength}-character limit");
            return false;
        }

        if (trimmed.Equals("mapped-project", StringComparison.OrdinalIgnoreCase))
        {
            if (!allowMappedProject)
            {
                rejection = new FocusedEvidenceRejection(
                    FocusedEvidenceRejectionCode.UnsafeFilter,
                    expression,
                    "mapped-project evidence requires an explicit supported project alias");
                return false;
            }

            // This is the bounded representation of a deterministic planner check that has no
            // narrower class filter. It runs one known test project, never the solution-level
            // acceptance manifest, and counts as one broker target.
            targetCount = 1;
            return true;
        }

        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("full", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("full-suite", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(".sln", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('*', StringComparison.Ordinal))
        {
            rejection = new FocusedEvidenceRejection(
                FocusedEvidenceRejectionCode.UnsafeFilter,
                expression,
                "unbounded evidence request rejected; use focused FullyQualifiedName~TestClass filters only");
            return false;
        }

        if (trimmed.Contains("FullyQualifiedName~", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = new List<FocusedEvidenceFilterToken>();
            foreach (var rawToken in Regex.Split(expression, @"[&|]"))
            {
                var originalToken = rawToken;
                var token = originalToken.Trim().Trim('(', ')').Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                var fullyQualifiedName = Regex.Match(
                    token,
                    @"^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$",
                    RegexOptions.IgnoreCase);
                if (fullyQualifiedName.Success)
                {
                    var value = fullyQualifiedName.Groups["value"].Value;
                    if (fullyQualifiedName.Groups["op"].Value == "!~")
                    {
                        tokens.Add(new FocusedEvidenceFilterToken(
                            originalToken,
                            $"FullyQualifiedName!~{value}",
                            FocusedEvidenceTokenKind.ExcludedClass,
                            value,
                            value));
                        continue;
                    }

                    if (!TryResolveFocusedEvidenceSelection(
                            worktreePath,
                            project,
                            originalToken,
                            value,
                            out var selection,
                            out rejection))
                    {
                        return false;
                    }

                    tokens.Add(selection);
                    targetCount++;
                    continue;
                }

                var categoryExclusion = Regex.Match(
                    token,
                    @"^Category\s*!=\s*(?<value>[A-Za-z_][A-Za-z0-9_.-]*)$",
                    RegexOptions.IgnoreCase);
                if (categoryExclusion.Success)
                {
                    var value = categoryExclusion.Groups["value"].Value;
                    tokens.Add(new FocusedEvidenceFilterToken(
                        originalToken,
                        $"Category!={value}",
                        FocusedEvidenceTokenKind.ExcludedTrait,
                        value,
                        string.Empty));
                    continue;
                }

                rejection = new FocusedEvidenceRejection(
                    FocusedEvidenceRejectionCode.UnsupportedToken,
                    originalToken,
                    $"focused evidence filter contains unsupported token '{originalToken}'");
                return false;
            }

            if (targetCount == 0)
            {
                rejection = new FocusedEvidenceRejection(
                    FocusedEvidenceRejectionCode.UnsafeFilter,
                    expression,
                    "focused evidence filter did not name a positive test selection");
                return false;
            }

            filter = new FocusedEvidenceFilter(
                expression,
                Regex.Replace(trimmed, @"\s+", string.Empty),
                tokens);
            return true;
        }

        var classNames = expression
            .Split([',', '|'], StringSplitOptions.None)
            .Select(original => (Original: original, Normalized: original.Trim()))
            .Where(token => token.Normalized.Length > 0)
            .ToArray();
        if (classNames.Length == 0 ||
            classNames.Any(token => !Regex.IsMatch(token.Normalized, @"^[A-Za-z_][A-Za-z0-9_.]*$")))
        {
            var offendingToken = classNames.FirstOrDefault(token =>
                !Regex.IsMatch(token.Normalized, @"^[A-Za-z_][A-Za-z0-9_.]*$")).Original ?? expression;
            rejection = new FocusedEvidenceRejection(
                FocusedEvidenceRejectionCode.UnsupportedToken,
                offendingToken,
                $"focused evidence request contains unsupported token '{offendingToken}'");
            return false;
        }

        targetCount = classNames.Length;
        filter = new FocusedEvidenceFilter(
            expression,
            string.Join("|", classNames.Select(token => $"FullyQualifiedName~{token.Normalized}")),
            classNames.Select(token => new FocusedEvidenceFilterToken(
                token.Original,
                $"FullyQualifiedName~{token.Normalized}",
                FocusedEvidenceTokenKind.Class,
                token.Normalized,
                token.Normalized)).ToArray());
        return true;
    }

    internal static bool TryResolveFocusedEvidenceSelection(
        string worktreePath,
        string project,
        string originalToken,
        string value,
        out FocusedEvidenceFilterToken selection,
        out FocusedEvidenceRejection rejection)
    {
        selection = null!;
        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnresolvableSelection,
            originalToken,
            $"focused evidence selection '{originalToken}' could not be resolved");
        var segments = value.Split('.');
        if (segments.Length == 1)
        {
            selection = new FocusedEvidenceFilterToken(
                originalToken,
                $"FullyQualifiedName~{value}",
                FocusedEvidenceTokenKind.Class,
                value,
                value);
            return true;
        }

        var candidates = new List<FocusedEvidenceFilterToken>();
        for (var classSegmentCount = 1; classSegmentCount <= segments.Length; classSegmentCount++)
        {
            var className = string.Join('.', segments.Take(classSegmentCount));
            var classFiles = FindFocusedEvidenceClassFiles(worktreePath, project, className);
            if (classFiles.Count == 0)
            {
                continue;
            }

            if (classSegmentCount == segments.Length)
            {
                candidates.Add(new FocusedEvidenceFilterToken(
                    originalToken,
                    $"FullyQualifiedName~{value}",
                    FocusedEvidenceTokenKind.Class,
                    value,
                    className));
                continue;
            }

            if (classSegmentCount != segments.Length - 1)
            {
                continue;
            }

            var methodSelector = segments[^1];
            if (FindFocusedEvidenceTestMethodNames(classFiles, className).Any(methodName =>
                    methodName.StartsWith(methodSelector, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(new FocusedEvidenceFilterToken(
                    originalToken,
                    $"FullyQualifiedName~{value}",
                    FocusedEvidenceTokenKind.Method,
                    value,
                    className));
            }
        }

        if (candidates.Count == 1)
        {
            selection = candidates[0];
            return true;
        }

        rejection = new FocusedEvidenceRejection(
            FocusedEvidenceRejectionCode.UnresolvableSelection,
            originalToken,
            candidates.Count == 0
                ? $"focused evidence selection '{originalToken}' does not resolve to a class or method in {GoalAcceptanceVerifier.ProjectLabel(project)}"
                : $"focused evidence selection '{originalToken}' is ambiguous in {GoalAcceptanceVerifier.ProjectLabel(project)}");
        return false;
    }

    internal static IReadOnlyList<string> FindFocusedEvidenceTestMethodNames(
        IReadOnlyList<string> classFiles,
        string className)
    {
        return classFiles
            .SelectMany(path => ParseCSharpRoot(path)
                .DescendantNodes()
                .OfType<TypeDeclarationSyntax>())
            .Where(declaration => FocusedEvidenceTypeMatches(declaration, className))
            .SelectMany(declaration => declaration.Members.OfType<MethodDeclarationSyntax>())
            .Where(method => method.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => IsFocusedEvidenceTestAttribute(attribute.Name.ToString())))
            .Select(method => method.Identifier.ValueText)
            .ToArray();
    }

    private static bool IsFocusedEvidenceTestAttribute(string attributeName)
    {
        var simpleName = attributeName.Split('.').Last();
        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            simpleName = simpleName[..^"Attribute".Length];
        }

        return simpleName is "Fact" or "Theory";
    }
}
