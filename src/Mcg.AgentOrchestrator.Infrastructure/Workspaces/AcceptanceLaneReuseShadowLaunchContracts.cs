using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceLaneReuseShadowLaunchSite(
    string MemberClass, string SourcePath, int Line, string SinkName, string? Target, string Kind);
internal sealed record AcceptanceLaneReuseShadowLaunchContract(string FullName,
    IReadOnlyList<AcceptanceLaneReuseShadowLaunchSite> LaunchSites,
    IReadOnlyList<AcceptanceLaneReuseShadowLaunchSite> RepositoryReads);

// Syntax-only, attempt-local evidence. Simple-name traversal deliberately over-approximates overloads.
internal static class AcceptanceLaneReuseShadowLaunchContracts
{
    internal static IReadOnlyList<(string Path, string Text)> ReadSources(string worktreePath) =>
        new[] { "Mcg.AgentOrchestrator.Infrastructure.Tests", "Mcg.AgentOrchestrator.TestSupport" }
            .Select(project => Path.Combine(worktreePath, "tests", project))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(worktreePath, path).Replace('\\', '/'), File.ReadAllText(path)))
            .ToArray();

    internal static IReadOnlyList<AcceptanceLaneReuseShadowLaunchContract> Build(
        IReadOnlyList<(string Path, string Text)> files, IReadOnlyList<AcceptanceTestClassSource> inventory)
    {
        var roots = files.Select(file =>
        {
            var tree = CSharpSyntaxTree.ParseText(file.Text, path: file.Path.Replace('\\', '/'));
            if (tree.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException($"Cannot parse launch-contract source '{file.Path}'.");
            return tree.GetRoot();
        }).ToArray();
        var types = roots.SelectMany(root => root.DescendantNodes().OfType<TypeDeclarationSyntax>()).ToArray();
        var byFullName = types.GroupBy(NameOf, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byShortName = types.GroupBy(type => type.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var callables = types.SelectMany(type => type.Members).Where(member =>
                member is MethodDeclarationSyntax or ConstructorDeclarationSyntax)
            .GroupBy(member => member is MethodDeclarationSyntax method ? method.Identifier.ValueText :
                ((ConstructorDeclarationSyntax)member).Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Cast<SyntaxNode>().ToArray(), StringComparer.Ordinal);

        return inventory.Select(item =>
        {
            var queue = new Queue<SyntaxNode>();
            var ancestors = new HashSet<TypeDeclarationSyntax>();
            void Seed(TypeDeclarationSyntax type)
            {
                if (!ancestors.Add(type)) return;
                foreach (var member in type.Members)
                {
                    if (member is ConstructorDeclarationSyntax || member is MethodDeclarationSyntax method &&
                        (method.Identifier.ValueText is "InitializeAsync" or "DisposeAsync" or "Dispose" ||
                         method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                             SimpleName(attribute.Name) is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute")))
                        queue.Enqueue(member);
                    if (member is FieldDeclarationSyntax field)
                        foreach (var variable in field.Declaration.Variables)
                            if (variable.Initializer is not null) queue.Enqueue(variable.Initializer);
                    if (member is PropertyDeclarationSyntax { Initializer: not null } property)
                        queue.Enqueue(property.Initializer);
                }
                foreach (var baseType in type.BaseList?.Types ?? [])
                    if (byShortName.TryGetValue(SimpleName(baseType.Type), out var parents))
                        foreach (var parent in parents) Seed(parent);
            }
            if (!byFullName.TryGetValue(item.FullName, out var declarations))
                throw new InvalidDataException($"Missing launch-contract declaration '{item.FullName}'.");
            foreach (var declaration in declarations) Seed(declaration);
            var visited = new HashSet<SyntaxNode>();
            var launches = new List<AcceptanceLaneReuseShadowLaunchSite>();
            var reads = new List<AcceptanceLaneReuseShadowLaunchSite>();
            void Follow(string name)
            {
                if (callables.TryGetValue(name, out var members))
                    foreach (var member in members) queue.Enqueue(member);
            }
            while (queue.TryDequeue(out var member))
            {
                if (!visited.Add(member)) continue;
                var nodes = member.DescendantNodesAndSelf().ToArray();
                var creations = nodes.OfType<ObjectCreationExpressionSyntax>().ToArray();
                var createsProcess = creations.Any(creation => SimpleName(creation.Type) is "Process" or "ProcessStartInfo");
                foreach (var invocation in nodes.OfType<InvocationExpressionSyntax>())
                {
                    var name = SimpleName(invocation.Expression);
                    var receiver = invocation.Expression is MemberAccessExpressionSyntax access ? access.Expression.ToString() : "";
                    var terminalGit = receiver == "GitCli" && name == "Run" ||
                        (receiver is "InfrastructureTestSupport" or "") && name == "RunGitProbe";
                    var sink = terminalGit || name == "Start" &&
                        (receiver is "Process" or "System.Diagnostics.Process" ||
                         invocation.ArgumentList.Arguments.Count == 0 && createsProcess) ||
                        receiver is "OwnedProcessGroup" or "ProcessTreeGuiSuppression" or "WorkerProcessJobs" or "WorkerProcessRunner";
                    if (sink)
                    {
                        var (target, kind) = terminalGit ? ("git", "external-tool") : ResolveTarget(invocation, nodes, creations);
                        launches.Add(Site(invocation, invocation.Expression.ToString(), target, kind));
                    }
                    // Terminal wrappers must not pull in unrelated same-name methods.
                    if (!terminalGit) Follow(name);
                }
                foreach (var creation in creations) Follow(SimpleName(creation.Type));
                foreach (var identifier in nodes.OfType<IdentifierNameSyntax>())
                {
                    var name = identifier.Identifier.ValueText;
                    if (name is "VerifiedRepositoryRoot" or "FindRepositoryRoot" or "CallerFilePath" or "CallerFilePathAttribute")
                        reads.Add(Site(identifier, name, null, "repository-read"));
                    // Invocation targets were handled above; remaining identifiers include method groups.
                    if (!identifier.Ancestors().OfType<InvocationExpressionSyntax>()
                        .Any(call => call.Expression == identifier ||
                            call.Expression is MemberAccessExpressionSyntax access && access.Name == identifier ||
                            call.Expression is MemberBindingExpressionSyntax binding && binding.Name == identifier ||
                            call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })) Follow(name);
                }
            }
            return new AcceptanceLaneReuseShadowLaunchContract(item.FullName, Ordered(launches), Ordered(reads));
        }).ToArray();
    }

    private static (string? Target, string Kind) ResolveTarget(InvocationExpressionSyntax invocation,
        SyntaxNode[] nodes, ObjectCreationExpressionSyntax[] creations)
    {
        var expression = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (HasBaseDirectory(expression)) return (expression!.ToString(), "repo-binary");
        if (Literal(expression) is { } direct) return (direct, Kind(direct));
        var targets = creations.Where(creation => SimpleName(creation.Type) == "ProcessStartInfo")
            .SelectMany(creation =>
            {
                var fileNames = creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                    .Where(assignment => SimpleName(assignment.Left) == "FileName")
                    .Select(assignment => assignment.Right).ToArray() ?? [];
                return fileNames.Length > 0 ? fileNames :
                    creation.ArgumentList?.Arguments.Take(1).Select(argument => argument.Expression).ToArray() ?? [];
            })
            .Concat(nodes.OfType<AssignmentExpressionSyntax>()
                .Where(assignment => SimpleName(assignment.Left) == "FileName" &&
                    creations.Any(creation => SimpleName(creation.Type) == "ProcessStartInfo"))
                .Select(assignment => assignment.Right)).Distinct().ToArray();
        if (targets.Any(HasBaseDirectory)) return (targets.First(HasBaseDirectory).ToString(), "repo-binary");
        var literals = targets.Select(Literal).Distinct(StringComparer.Ordinal).ToArray();
        if (literals.Length == 1 && literals[0] is { } target) return (target, Kind(target));
        return (expression?.ToString(), "unresolved");
    }

    private static bool HasBaseDirectory(SyntaxNode? node) => node?.DescendantNodesAndSelf()
        .OfType<MemberAccessExpressionSyntax>().Any(access => access.Expression.ToString() is "AppContext" or "System.AppContext" &&
            access.Name.Identifier.ValueText == "BaseDirectory") == true;
    private static string? Literal(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText : null;
    private static string Kind(string target) => target switch
    {
        "git" or "git.exe" or "cmd" or "cmd.exe" or "sh" or "pwsh" or "pwsh.exe" or "powershell" or "powershell.exe" => "external-tool",
        "dotnet" => "repo-binary",
        _ when target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) => "repo-binary",
        _ => "unresolved"
    };
    private static string SimpleName(SyntaxNode node) => node switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        QualifiedNameSyntax qualified => SimpleName(qualified.Right),
        AliasQualifiedNameSyntax alias => SimpleName(alias.Name),
        _ => node.ToString()
    };
    private static string NameOf(TypeDeclarationSyntax type) => string.Join(".",
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString())
            .Append(string.Join("+", type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
                .Select(parent => parent.Identifier.ValueText).Append(type.Identifier.ValueText))));
    private static AcceptanceLaneReuseShadowLaunchSite Site(SyntaxNode node, string name, string? target, string kind) =>
        new(NameOf(node.Ancestors().OfType<TypeDeclarationSyntax>().First()), node.SyntaxTree.FilePath,
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, name, target, kind);
    private static AcceptanceLaneReuseShadowLaunchSite[] Ordered(IEnumerable<AcceptanceLaneReuseShadowLaunchSite> sites) =>
        sites.OrderBy(site => site.SourcePath, StringComparer.Ordinal).ThenBy(site => site.Line)
            .ThenBy(site => site.SinkName, StringComparer.Ordinal).ToArray();
}
