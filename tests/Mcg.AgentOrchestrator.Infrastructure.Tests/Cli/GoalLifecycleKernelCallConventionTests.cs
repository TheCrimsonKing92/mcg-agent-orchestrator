using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class GoalLifecycleKernelCallConventionTests
{
    private const string CliDirectory = "src/Mcg.AgentOrchestrator.App/Cli/";
    private static readonly HashSet<string> LifecycleMethods =
        ["CancelGoal", "SupersedeGoal", "ParkGoal", "UnparkGoal"];
    private static readonly HashSet<(string Path, string Method)> AllowedMembers =
    [
        (CliDirectory + "CliCommandHandlers.Goals.CancelTransition.cs", "ApplyGoalCancelWithoutRendering"),
        (CliDirectory + "CliCommandHandlers.Goals.SupersedeTransition.cs", "ApplyGoalSupersedeWithoutRendering"),
        (CliDirectory + "CliCommandHandlers.Goals.ParkTransition.cs", "ApplyGoalParkWithoutRendering"),
        (CliDirectory + "CliCommandHandlers.Goals.LifecycleTransition.cs", "ApplyGoalUnparkWithoutRendering"),
        (CliDirectory + "CliCommandHandlers.Goals.AbandonTransition.cs", "ApplyGoalAbandonWithoutRendering"),
        (CliDirectory + "CliCommandHandlers.Goals.StopTransitions.cs", "ValidateGoalParkStopAlias"),
        (CliDirectory + "CliCommandHandlers.Goals.StopTransitions.cs", "ApplyGoalParkStopAliasWithoutRendering"),
        (CliDirectory + "CliCommandHandlers.Goals.StopTransitions.cs", "ApplyGoalAbandonStopAliasWithoutRendering")
    ];
    private static readonly HashSet<string> AllowedFiles =
    [
        "src/Mcg.AgentOrchestrator.App/Orchestration/GoalAbandonPlanner.cs",
        "src/Mcg.AgentOrchestrator.App/Orchestration/GoalsPrunePlanner.cs"
    ];

    [Xunit.Fact]
    public void AppSources_CallKernelLifecycleMethodsOnlyFromOwningDecisions()
    {
        var root = CliVerifiedRepositoryRoot.Find();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src/Mcg.AgentOrchestrator.App"), "*.cs",
                SearchOption.AllDirectories)
            .Select(path => (FullPath: path, Path: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(file => !file.Path.Split('/').Any(part => part is
                "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or "TestResults" or "playwright-report"))
            .Select(file => (file.Path, Text: File.ReadAllText(file.FullPath)))
            .ToArray();

        Xunit.Assert.NotEmpty(files);
        Xunit.Assert.Contains(files, file => file.Path == "src/Mcg.AgentOrchestrator.App/Orchestration/GoalAbandonPlanner.cs");
        var violations = FindViolations(files);
        Xunit.Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Xunit.Fact]
    public void SeededViolation_ReportsDisallowedParkGoalCall()
    {
        var violations = FindViolations([("src/Mcg.AgentOrchestrator.App/Cli/Disallowed.cs",
            "class Disallowed { void Stop(dynamic kernel, dynamic id) { kernel.ParkGoal(id, \"reason\"); } }")]);
        Xunit.Assert.Equal("src/Mcg.AgentOrchestrator.App/Cli/Disallowed.cs:1: Stop calls ParkGoal", Xunit.Assert.Single(violations));
    }

    [Xunit.Theory]
    [Xunit.InlineData("kernel.CancelGoal(id, \"reason\")", "CancelGoal")]
    [Xunit.InlineData("kernel?.SupersedeGoal(id, \"reason\")", "SupersedeGoal")]
    [Xunit.InlineData("ParkGoal(id, \"reason\")", "ParkGoal")]
    [Xunit.InlineData("kernel.UnparkGoal(id, \"reason\")", "UnparkGoal")]
    public void CallForms_AreDetected(string call, string method)
    {
        var violations = FindViolations([("src/Mcg.AgentOrchestrator.App/Other.cs",
            $"class Other {{ void Stop(dynamic kernel, dynamic id) {{ {call}; }} }}")]);
        Xunit.Assert.Contains($"Stop calls {method}", Xunit.Assert.Single(violations));
    }

    [Xunit.Fact]
    public void AllowedMember_DoesNotAuthorizeOtherMethodsOrLocalFunctions()
    {
        const string source = """
            class Handler {
                void ApplyGoalParkWithoutRendering(dynamic kernel, dynamic id) {
                    kernel.ParkGoal(id, "allowed");
                    void Bypass() { kernel.ParkGoal(id, "disallowed"); }
                }
                void Other(dynamic kernel, dynamic id) { kernel.ParkGoal(id, "disallowed"); }
            }
            """;
        var violations = FindViolations([(CliDirectory + "CliCommandHandlers.Goals.ParkTransition.cs", source)]);
        Xunit.Assert.Equal(2, violations.Count);
        Xunit.Assert.Contains(violations, violation => violation.Contains("Bypass calls ParkGoal", StringComparison.Ordinal));
        Xunit.Assert.Contains(violations, violation => violation.Contains("Other calls ParkGoal", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CommentsAndStrings_DoNotCountAsCalls()
    {
        var violations = FindViolations([("src/Mcg.AgentOrchestrator.App/Other.cs", """
            class Other {
                // kernel.ParkGoal(id, "reason");
                string Text = "kernel.CancelGoal(id, reason)";
            }
            """)]);
        Xunit.Assert.Empty(violations);
    }

    private static IReadOnlyList<string> FindViolations(IEnumerable<(string Path, string Text)> files)
    {
        var violations = new List<string>();
        foreach (var (path, source) in files)
        {
            var tree = CSharpSyntaxTree.ParseText(source, path: path);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => null
                };
                if (name is null || !LifecycleMethods.Contains(name) || AllowedFiles.Contains(path)) continue;
                var owner = call.Ancestors().FirstOrDefault(node => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax);
                var method = owner switch
                {
                    MethodDeclarationSyntax declaration => declaration.Identifier.ValueText,
                    LocalFunctionStatementSyntax local => local.Identifier.ValueText,
                    _ => "<no method>"
                };
                if (AllowedMembers.Contains((path, method))) continue;
                var line = tree.GetLineSpan(call.Span).StartLinePosition.Line + 1;
                violations.Add($"{path}:{line}: {method} calls {name}");
            }
        }
        return violations;
    }
}
