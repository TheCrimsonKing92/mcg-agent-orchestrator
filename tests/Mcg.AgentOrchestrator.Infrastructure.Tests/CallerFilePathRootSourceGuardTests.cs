using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class CallerFilePathRootSourceGuardTests
{
    private static readonly IReadOnlyDictionary<string, string> Exceptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierSplitFactParityTests.cs"] =
                "Reads the baseline beside the source file compiled into the executing assembly."
        };

    private static readonly HashSet<string> ApprovedExceptionPaths =
    [
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalAcceptanceVerifierSplitFactParityTests.cs"
    ];

    private static readonly HashSet<string> HelperPaths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/VerifiedRepositoryRoot.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/VerifiedRepositoryRoot.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/VerifiedRepositoryRoot.cs"
    ];

    [Xunit.Fact]
    public void CallerPathRootDiscoveryUsesVerifiedRepositoryRoot()
    {
        var repositoryRoot = VerifiedRepositoryRoot.Find();
        var activeExceptions = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var project in new[]
                 {
                     "Mcg.AgentOrchestrator.Core.Tests",
                     "Mcg.AgentOrchestrator.Infrastructure.Tests"
                 })
        {
            var projectRoot = Path.Combine(repositoryRoot, "tests", project);
            foreach (var path in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "bin" or "obj" or "artifacts" or ".scratch" or "TestResults"))
                    continue;
                if (HelperPaths.Contains(relative))
                    continue;

                var methods = CallerPathMethods(File.ReadAllText(path));
                if (Exceptions.ContainsKey(relative))
                {
                    if (methods.Count != 0)
                        activeExceptions.Add(relative);
                    continue;
                }

                violations.AddRange(methods.Where(method => !method.ToFullString().Contains("VerifiedRepositoryRoot.", StringComparison.Ordinal))
                    .Select(method => $"{relative}: {method.Identifier.ValueText} bypasses VerifiedRepositoryRoot"));
            }
        }

        Assert.All(Exceptions, entry =>
        {
            Assert.Contains(entry.Key, ApprovedExceptionPaths);
            Assert.False(string.IsNullOrWhiteSpace(entry.Value));
            Assert.Contains(entry.Key, activeExceptions);
        });
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Xunit.Fact]
    public void SourceGuardDetectsAnUnroutedCallerPathMethod()
    {
        const string source = "using System.Runtime.CompilerServices; class Example { string Root([CallerFilePath] string path = \"\") => path; }";
        Assert.Single(CallerPathMethods(source));
        Assert.Empty(CallerPathMethods("class Example { string Text = \"[CallerFilePath] string path\"; }"));
    }

    [Xunit.Fact]
    public void HelperCopiesStayIdentical()
    {
        var repositoryRoot = VerifiedRepositoryRoot.Find();
        var expected = File.ReadAllText(Path.Combine(repositoryRoot, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "VerifiedRepositoryRoot.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var path in new[]
                 {
                     Path.Combine(repositoryRoot, "tests", "Mcg.AgentOrchestrator.Core.Tests", "VerifiedRepositoryRoot.cs"),
                     Path.Combine(repositoryRoot, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Cli", "VerifiedRepositoryRoot.cs")
                 })
            Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static IReadOnlyList<MethodDeclarationSyntax> CallerPathMethods(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.ParameterList.Parameters.Any(parameter =>
                parameter.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
                    attribute.Name.ToString().EndsWith("CallerFilePath", StringComparison.Ordinal) ||
                    attribute.Name.ToString().EndsWith("CallerFilePathAttribute", StringComparison.Ordinal))))
            .ToArray();
}
