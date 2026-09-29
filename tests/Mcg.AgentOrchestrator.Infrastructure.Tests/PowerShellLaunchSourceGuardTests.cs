using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class PowerShellLaunchSourceGuardTests
{
    private const int InitialAllowListCount = 2;
    private static readonly string[] AllowedTheorySelectors =
    [
        "MtpTestRunnerScriptTestsCompilerBoundary.cs:CompilerFrontendsPreserveLongCacheIdentityAndReapOwnedDirectories",
        "MtpTestRunnerScriptTestsCompilerBoundary.cs:CompilerFailureReapsCompilerAndCacheStaging"
    ];

    [Fact]
    public void NoTestSourceLaunchesPwshFromPath()
    {
        Assert.True(AllowedTheorySelectors.Length <= InitialAllowListCount);
        var root = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        var violations = new List<string>();
        var foundSelectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj" or ".scratch" or ".orchestrator-prototype") ||
                relative == nameof(PowerShellLaunchSourceGuardTests) + ".cs") continue;
            foreach (var (line, selector) in FindLaunches(File.ReadAllText(path)))
            {
                var key = selector is null ? null : $"{relative}:{selector}";
                if (key is not null && AllowedTheorySelectors.Contains(key)) foundSelectors.Add(key);
                else violations.Add($"{relative}:{line}");
            }
        }
        Assert.Equal(AllowedTheorySelectors.Order(), foundSelectors.Order());
        Assert.True(violations.Count == 0, "PATH pwsh launches: " + string.Join(", ", violations));
    }

    [Fact]
    public void DetectorFindsLaunchesWithoutFlaggingInspectionData()
    {
        var source = "class C { void M() { var p = new ProcessStartInfo { FileName = \"pwsh\" }; " +
            "RunProcess(root, \"pwsh\", args); Run(root, path, \"pwsh\", body); " +
            "foreach (var n in new[] { \"pwsh\" }) Process.Start(n); " +
            "var shells = new[] { \"pwsh\" }; foreach (var n in shells) Process.Start(n); " +
            "var direct = new ProcessStartInfo(\"pwsh\"); " +
            "Assert.Equal(\"pwsh\", name); } }";
        Assert.Equal(6, FindLaunches(source).Count());
    }

    private static IEnumerable<(int Line, string? Selector)> FindLaunches(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>()
                     .Where(node => node.Token.ValueText is "pwsh" or "pwsh.exe"))
        {
            string? selector = null;
            var attribute = literal.Ancestors().OfType<AttributeSyntax>().FirstOrDefault();
            if (attribute?.Name.ToString().EndsWith("InlineData", StringComparison.Ordinal) == true)
                selector = literal.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
            var assignment = literal.Parent as AssignmentExpressionSyntax;
            var argument = literal.Parent as ArgumentSyntax;
            var call = argument?.Parent?.Parent as InvocationExpressionSyntax;
            var name = call?.Expression.ToString().Split('.').Last();
            var creation = argument?.Parent?.Parent as ObjectCreationExpressionSyntax;
            var foreachNode = literal.Ancestors().OfType<ForEachStatementSyntax>().FirstOrDefault();
            var isProbe = foreachNode is not null && foreachNode.Statement.ToString().Contains("Process.Start", StringComparison.Ordinal);
            var declarator = literal.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
            var method = literal.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var isNamedProbe = declarator is not null && method?.DescendantNodes().OfType<ForEachStatementSyntax>().Any(loop =>
                loop.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.ValueText == declarator.Identifier.ValueText &&
                loop.Statement.ToString().Contains("Process.Start", StringComparison.Ordinal)) == true;
            if (selector is null &&
                !(assignment?.Left.ToString().EndsWith("FileName", StringComparison.Ordinal) == true) &&
                !(name is not null && (name.StartsWith("Run", StringComparison.Ordinal) || name.StartsWith("Start", StringComparison.Ordinal))) &&
                !(creation?.Type.ToString().EndsWith("ProcessStartInfo", StringComparison.Ordinal) == true) &&
                !isProbe && !isNamedProbe) continue;
            yield return (tree.GetLineSpan(literal.Span).StartLinePosition.Line + 1, selector);
        }
    }
}
