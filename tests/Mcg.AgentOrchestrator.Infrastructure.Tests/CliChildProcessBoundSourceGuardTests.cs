using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

public sealed class CliChildProcessBoundSourceGuardTests
{
    private static readonly string[] Sources =
    [
        "Cli/CliAttentionNextReadOnlyWriterHeldTests.cs",
        "CliGoalEventsStatusReadOnlyTests.cs",
        "Cli/QueryOnlyCompositionTests.cs",
        "GoalRefinementStartupValidationTests.cs",
        "CliHelpTests.cs"
    ];

    [Fact]
    public void CliChildSitesUseSharedRunnerWithoutShortExitBounds()
    {
        var root = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        foreach (var source in Sources)
        {
            var path = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Required CLI child source is missing: {path}");
            var code = File.ReadAllText(path);
            Assert.Contains("CliChildProcessRunner.", code, StringComparison.Ordinal);
            Assert.Empty(FindViolations(code).Select(reason => $"{source}: {reason}"));
        }
    }

    [Fact]
    public void DetectorRejectsOriginalShortBoundsAndDirectCliStart()
    {
        const string oldSeconds = "class C { void M() { var start = new ProcessStartInfo(\"dotnet\"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var process = Process.Start(start); } }";
        const string oldMilliseconds = "class C { void M() { var start = new ProcessStartInfo(\"dotnet\"); process.WaitForExit(30000); } }";
        const string oldCancelAfter = "class C { void M() { var start = new ProcessStartInfo(\"dotnet\"); token.CancelAfter(TimeSpan.FromMinutes(1)); } }";
        const string allowed = "class C { void M() { var start = new ProcessStartInfo(\"dotnet\"); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var result = CliChildProcessRunner.Run(start); } void Sentinel() { var worker = Process.Start(workerInfo); var text = \"60 seconds\"; } }";

        Assert.Contains(FindViolations(oldSeconds), reason => reason.Contains("short", StringComparison.Ordinal));
        Assert.Contains(FindViolations(oldSeconds), reason => reason.Contains("Process.Start", StringComparison.Ordinal));
        Assert.Contains(FindViolations(oldMilliseconds), reason => reason.Contains("short", StringComparison.Ordinal));
        Assert.Contains(FindViolations(oldCancelAfter), reason => reason.Contains("short", StringComparison.Ordinal));
        Assert.Empty(FindViolations(allowed));
    }

    private static IReadOnlyList<string> FindViolations(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var violations = new List<string>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var text = method.ToString();
            if (!text.Contains("\"dotnet\"", StringComparison.Ordinal) &&
                !text.Contains("Mcg.AgentOrchestrator.App.dll", StringComparison.Ordinal) &&
                !text.Contains("typeof(CliArgumentParser).Assembly.Location", StringComparison.Ordinal))
                continue;

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression is MemberAccessExpressionSyntax access
                    ? access.Name.Identifier.ValueText : string.Empty;
                if (invocation.Expression.ToString() == "Process.Start")
                    violations.Add($"{method.Identifier}: direct CLI Process.Start");
                if (name is "WaitForExit" or "WaitForExitAsync" or "CancelAfter" &&
                    invocation.ArgumentList.Arguments.Count > 0 &&
                    IsShort(invocation.ArgumentList.Arguments[0].Expression))
                    violations.Add($"{method.Identifier}: short {name} bound");
            }

            foreach (var creation in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (creation.Type.ToString() == "CancellationTokenSource" &&
                    creation.ArgumentList?.Arguments.Count > 0 &&
                    IsShort(creation.ArgumentList.Arguments[0].Expression))
                    violations.Add($"{method.Identifier}: short CancellationTokenSource bound");
            }
        }
        return violations;
    }

    private static bool IsShort(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal &&
            double.TryParse(literal.Token.ValueText, out var milliseconds))
            return milliseconds < 120_000;

        if (expression is not InvocationExpressionSyntax invocation ||
            invocation.Expression is not MemberAccessExpressionSyntax access ||
            access.Expression.ToString() != "TimeSpan" ||
            invocation.ArgumentList.Arguments.Count != 1 ||
            invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax value ||
            !double.TryParse(value.Token.ValueText, out var number))
            return false;

        return access.Name.Identifier.ValueText switch
        {
            "FromMilliseconds" => number < 120_000,
            "FromSeconds" => number < 120,
            "FromMinutes" => number < 2,
            _ => false
        };
    }
}
