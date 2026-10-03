using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using FocusedEvidenceFilter = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilter;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal sealed partial class AcceptanceManifestCheck
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<FocusedEvidenceMatchedClassSelection> FocusedEvidenceMatchedClasses { get; init; } = [];
    }
}

internal sealed record FocusedEvidenceMatchedClassSelection(string CanonicalToken, IReadOnlyList<string> ClassNames);

internal static partial class FocusedEvidenceRequestResolver
{
    private static IEnumerable<string> EnumerateFocusedEvidenceSourceFiles(string worktreePath, string project)
    {
        var projectPath = Path.Combine(worktreePath, project.Replace('/', Path.DirectorySeparatorChar));
        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)));
    }

    private static FocusedEvidenceBudget MeasureFocusedEvidenceBudget(
        string worktreePath,
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> validated,
        int totalTargets)
    {
        var matchesByProject = new Dictionary<string, Dictionary<string, IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        var expandedTargets = totalTargets;
        foreach (var group in validated.GroupBy(item => item.Project, StringComparer.OrdinalIgnoreCase))
        {
            var tokens = group.Where(item => item.Filter is not null)
                .SelectMany(item => item.Filter!.Tokens)
                .Where(token => token.Kind == FocusedEvidenceTokenKind.Class && !token.Value.Contains('.'))
                .ToArray();
            if (tokens.Length == 0)
            {
                continue;
            }

            var declarations = ReadFocusedEvidenceBudgetDeclarations(worktreePath, group.Key, tokens.Select(token => token.Value).ToArray());
            var selectedClasses = new HashSet<string>(StringComparer.Ordinal);
            var matchesByToken = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var unmatchedTerms = 0;
            foreach (var token in tokens)
            {
                var matches = declarations.Where(declaration => declaration.Value.Contains(token.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
                selectedClasses.UnionWith(matches.Select(declaration => declaration.Key));
                matchesByToken[token.CanonicalToken] = matches.Select(declaration => declaration.Value)
                    .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                if (matches.Length == 0)
                {
                    unmatchedTerms++;
                }
            }

            matchesByProject[group.Key] = matchesByToken;
            expandedTargets += selectedClasses.Count + unmatchedTerms - tokens.Length;
        }

        // Deduplicate overlapping/partial classes without reducing any historical request budget.
        return new FocusedEvidenceBudget(Math.Max(totalTargets, expandedTargets), matchesByProject);
    }

    private static IReadOnlyDictionary<string, string> ReadFocusedEvidenceBudgetDeclarations(
        string worktreePath, string project, IReadOnlyList<string> terms)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var path in EnumerateFocusedEvidenceSourceFiles(worktreePath, project))
            {
                var text = File.ReadAllText(path);
                if (!terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var root = CSharpSyntaxTree.ParseText(text, path: path).GetCompilationUnitRoot();
                foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                             .Where(declaration => declaration is ClassDeclarationSyntax or RecordDeclarationSyntax))
                {
                    var namespaceNames = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                        .Reverse().Select(item => item.Name.ToString());
                    var containingTypes = declaration.Ancestors().OfType<TypeDeclarationSyntax>()
                        .Reverse().Select(item => item.Identifier.ValueText);
                    var simpleName = declaration.Identifier.ValueText;
                    declarations[string.Join('.', namespaceNames.Concat(containingTypes).Append(simpleName))] = simpleName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This advisory scan must not reject a selector that resolved successfully above.
            return new Dictionary<string, string>();
        }

        return declarations;
    }

    private sealed record FocusedEvidenceBudget(
        int TargetCount,
        IReadOnlyDictionary<string, Dictionary<string, IReadOnlyList<string>>> MatchesByProject)
    {
        internal IReadOnlyList<FocusedEvidenceMatchedClassSelection> For(string project, IReadOnlyList<FocusedEvidenceFilter> filters)
        {
            var selections = new List<FocusedEvidenceMatchedClassSelection>();
            foreach (var token in filters.SelectMany(filter => filter.Tokens).Where(token => token.Kind == FocusedEvidenceTokenKind.Class))
            {
                IReadOnlyList<string> names = [];
                if (token.Value.Contains('.'))
                {
                    names = [token.ContainingClass.Split('.')[^1]];
                }
                else if (MatchesByProject.TryGetValue(project, out var matches) && matches.TryGetValue(token.CanonicalToken, out var matchedNames))
                {
                    names = matchedNames;
                }

                selections.Add(new FocusedEvidenceMatchedClassSelection(token.CanonicalToken, names));
            }

            return selections;
        }
    }
}
