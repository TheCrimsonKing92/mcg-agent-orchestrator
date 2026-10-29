using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance belongs to one repository's driver. Only unchanged test sources enter the cache;
// candidate edits are overlaid per selection and never become another candidate's baseline.
internal sealed class PreTesterChangedFileGuardClasses
{
    private sealed record GuardReference(string Class, string Method, string[] Path);
    private readonly object _sync = new();
    private string? _revision;
    private Dictionary<string, GuardReference[]> _sources = new(StringComparer.OrdinalIgnoreCase);

    internal DeveloperDeferredTestSelection Select(
        string worktreePath, IReadOnlyList<string> changedPaths, string mainRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainRevision);
        if (changedPaths.Count == 0) return new([], []);
        var changed = changedPaths.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(worktreePath, "tests");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        string[] names;
        lock (_sync)
        {
            // Publish a complete generation only after every source was read successfully.
            var sources = string.Equals(_revision, mainRevision, StringComparison.Ordinal)
                ? new Dictionary<string, GuardReference[]>(_sources, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GuardReference[]>(StringComparer.OrdinalIgnoreCase);
            var references = new List<GuardReference>();
            foreach (var file in DeveloperDeferredTestSelections.EnumerateSourceFiles(root)
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                var relative = Normalize(Path.GetRelativePath(worktreePath, file));
                if (changed.Contains(relative))
                    references.AddRange(ReadReferences(file));
                else
                {
                    if (!sources.TryGetValue(relative, out var cached))
                        sources[relative] = cached = ReadReferences(file);
                    references.AddRange(cached);
                }
            }
            names = references.Where(reference => changed.Any(path => Matches(path, reference.Path)))
                .Select(reference => reference.Class).Distinct(StringComparer.Ordinal).ToArray();
            _sources = sources;
            _revision = mainRevision;
        }
        // Resolution is candidate-specific, even when the reference map is cached.
        return DeveloperDeferredTestSelections.ResolveNames(worktreePath, names, requireUniqueSourceFile: true);
    }

    internal static bool ContainsClass(IReadOnlyList<FindingEvidenceSelection> selections, string? testClass) =>
        selections.Any(selection => string.Equals(selection.TestClass, testClass, StringComparison.Ordinal));

    private static bool Matches(string changed, string[] segments)
    {
        var path = string.Join('/', segments);
        return changed.Equals(path, StringComparison.OrdinalIgnoreCase) ||
               changed.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != "."));

    private static GuardReference[] ReadReferences(string file)
    {
        var source = File.ReadAllText(file);
        // TestSupport currently has no shared path-reading helper. Private helpers and arbitrary
        // methods named ReadSource are deliberately outside this literal-reading contract.
        if (!source.Contains("ReadAllText", StringComparison.Ordinal)) return [];
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var references = new List<GuardReference>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTest))
        {
            var declaringClass = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (declaringClass is null) continue;
            foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsCall(call, "File.ReadAllText", "System.IO.File.ReadAllText") ||
                    call.ArgumentList.Arguments.Count == 0) continue;
                var literals = LiteralSequence(call.ArgumentList.Arguments[0].Expression, method, 0).ToArray();
                var first = Array.FindIndex(literals, literal => literal is not null);
                if (first < 0 || literals.Skip(first).Any(literal => literal is null)) continue;
                var segments = Normalize(string.Join('/', literals.Skip(first))).Split('/');
                if (segments.Length == 0 || segments.Any(segment => segment is "" or "..")) continue;
                references.Add(new(declaringClass.Identifier.ValueText, method.Identifier.ValueText, segments));
            }
        }
        return references.ToArray();
    }

    private static bool IsTest(MethodDeclarationSyntax method) => method.AttributeLists
        .SelectMany(list => list.Attributes).Any(attribute =>
        {
            var name = attribute.Name.ToString().Split('.').Last();
            if (name.EndsWith("Attribute", StringComparison.Ordinal)) name = name[..^9];
            return name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal);
        });

    private static bool IsCall(InvocationExpressionSyntax call, params string[] names) =>
        names.Contains(call.Expression.ToString().Replace("global::", "", StringComparison.Ordinal), StringComparer.Ordinal);

    private static IEnumerable<string?> LiteralSequence(
        ExpressionSyntax expression, MethodDeclarationSyntax method, int depth)
    {
        if (depth > 16) return [null];
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            return LiteralSequence(parenthesized.Expression, method, depth + 1);
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return [literal.Token.ValueText];
        if (expression is InvocationExpressionSyntax combine && IsCall(combine, "Path.Combine", "System.IO.Path.Combine"))
            return combine.ArgumentList.Arguments.SelectMany(argument => LiteralSequence(argument.Expression, method, depth + 1));
        if (expression is IdentifierNameSyntax identifier)
        {
            var variables = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText).ToArray();
            var reassigned = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(assignment => assignment.Left is IdentifierNameSyntax target &&
                                   target.Identifier.ValueText == identifier.Identifier.ValueText);
            if (!reassigned && variables is [{ Initializer: not null } variable] && variable.SpanStart < expression.SpanStart)
                return LiteralSequence(variable.Initializer.Value, method, depth + 1);
        }
        return [null];
    }
}
