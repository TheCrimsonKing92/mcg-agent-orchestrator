using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

// Parallel-safe: repository source reads only; no shared state is changed.
public sealed class WholeTestTimeoutSourceGuardTests
{
    [Fact]
    public void TestAttributesUseNamedHangFailsafes()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var tests = Path.Combine(root, "tests");
        Assert.True(Directory.Exists(tests), $"Test source directory missing: {tests}");
        var files = EnumerateSourceFiles(tests).ToArray();
        Assert.NotEmpty(files);
        var failures = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WholeTestTimeoutSourceGuardTests.cs")
                continue;
            var source = File.ReadAllText(file);
            if (!source.Contains("Timeout", StringComparison.Ordinal))
                continue;
            failures.AddRange(FindNumericTimeoutAttributes(source)
                .Select(line => $"{relative}:{line}: numeric test Timeout; use a named hang failsafe."));
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("[Fact(Timeout = 30_000)]", 1)]
    [InlineData("[Xunit.Theory(Timeout = 30000)]", 1)]
    [InlineData("[Xunit.Fact(Timeout = 30_000, DisplayName = \"x\")]", 1)]
    [InlineData("[Fact(\n    Timeout = 60_000)]", 2)]
    [InlineData("[global::Xunit.FactAttribute(Timeout = (+0x7530))]", 1)]
    [InlineData("[Trait(\"a\", \"b\"), TheoryAttribute(Timeout = 30000L)]", 1)]
    [InlineData("[Fact(Timeout = 30 * 1000)]", 1)]
    [InlineData("[Fact(Timeout = (int)30_000)]", 1)]
    public void TimeoutDetectorRejectsNumericBounds(string attribute, int expectedLine)
    {
        var source = attribute + "\nclass C {}";
        Assert.Equal(new[] { expectedLine }, FindNumericTimeoutAttributes(source));
    }

    [Theory]
    [InlineData("[Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)] class C {}")]
    [InlineData("[Fact(DisplayName = \"waits 30000 ms\")] class C {}")]
    [InlineData("class C { object Connection = new Builder { DefaultTimeout = 30 }; }")]
    [InlineData("class C { string Sample = \"[Fact(Timeout = 30_000)]\"; }")]
    [InlineData("// [Fact(Timeout = 30_000)]\nclass C {}")]
    [InlineData("[Fact(DisplayName = \"[Fact(Timeout = 30000)]\")] class C {}")]
    [InlineData("[Fact(Timeout = SomeConstant * 2)] class C {}")]
    public void TimeoutDetectorAllowsNamedBoundsAndUnrelatedText(string source)
    {
        Assert.Empty(FindNumericTimeoutAttributes(source));
    }

    [Fact]
    public void WholeTestFailsafeKeepsHungTestNamesBelowLaneStallLimit()
    {
        Assert.Equal(300_000, TestHangGuard.WholeTestMilliseconds);
        Assert.True(TestHangGuard.WholeTestMilliseconds < AcceptanceLaneStallPolicy.StallLimitSeconds * 1_000);
    }

    [Fact]
    public void ExternalOwnedStartUsesSharedChildHangFailsafe()
    {
        var path = Path.Combine(InfrastructureTestSupport.FindRepositoryRoot(), "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "ConductorBatchLoopTestsParallelAcceptance.cs");
        Assert.True(File.Exists(path), $"External-child test source missing: {path}");
        var body = FindMethodBody(File.ReadAllText(path), "RunExternalOwnedStartScenario");
        Assert.False(string.IsNullOrWhiteSpace(body), $"RunExternalOwnedStartScenario declaration not found in {path}");
        Assert.Contains("OwnedAttemptExitWait.WaitForExit(externalProcess, initialAttempt)", body, StringComparison.Ordinal);
        var helperPath = Path.Combine(Path.GetDirectoryName(path)!, "OwnedAttemptExitWait.cs");
        Assert.True(File.Exists(helperPath), $"External-child wait helper source missing: {helperPath}");
        var helperBody = CSharpSyntaxTree.ParseText(File.ReadAllText(helperPath)).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "WaitForExit"
                && method.ParameterList.Parameters.Count == 5).Body?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(helperBody), $"WaitForExit declaration not found in {helperPath}");
        // The helper owns both the no-progress window and the total-wait failsafe.
        Assert.Contains("TestHangGuard.HangSafetyBound", helperBody, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(TestHangGuard.WholeTestMilliseconds)", helperBody, StringComparison.Ordinal);
        // Guard the external child only; the separate finally cleanup wait is outside this change's scope.
        Assert.Empty(FindNumericLiteralWaitForExitCalls(body!, receiver: "externalProcess"));
    }

    [Theory]
    [InlineData("WaitForExit(60000)")]
    [InlineData("WaitForExit(60_000)")]
    [InlineData("externalProcess.WaitForExit(milliseconds: 60_000)")]
    [InlineData("externalProcess.WaitForExit((+60_000))")]
    public void ExitWaitDetectorRejectsNumericBounds(string call)
    {
        Assert.Single(FindNumericLiteralWaitForExitCalls(call + ";"));
    }

    [Theory]
    [InlineData("WaitForExit()")]
    [InlineData("externalProcess.WaitForExit(TestHangGuard.HangSafetyBound)")]
    [InlineData("externalProcess.WaitForExit((int)TestHangGuard.HangSafetyBound.TotalMilliseconds)")]
    public void ExitWaitDetectorAllowsUntimedAndNamedBounds(string call)
    {
        Assert.Empty(FindNumericLiteralWaitForExitCalls(call + ";"));
    }

    [Fact]
    public void ExitWaitDetectorSeparatesExternalChildFromCleanup()
    {
        const string body = "externalProcess.WaitForExit(60000); process.WaitForExit(5000);";
        Assert.Equal("externalProcess.WaitForExit(60000)",
            Assert.Single(FindNumericLiteralWaitForExitCalls(body, receiver: "externalProcess")));
        Assert.Equal(2, FindNumericLiteralWaitForExitCalls(body).Count);
    }

    [Fact]
    public void MethodBodyDetectorFindsStaticDeclarationRatherThanCalls()
    {
        const string source = "class C { void Call() { Target(); } private static void Target() { if (true) { WaitForExit(60000); } } }";
        var body = FindMethodBody(source, "Target");
        Assert.NotNull(body);
        Assert.Single(FindNumericLiteralWaitForExitCalls(body));
        Assert.Null(FindMethodBody(source, "Missing"));
    }

    internal static IReadOnlyList<int> FindNumericTimeoutAttributes(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var lines = new List<int>();
        foreach (var attribute in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = attribute.Name.ToString();
            if (name.StartsWith("global::", StringComparison.Ordinal))
                name = name[8..];
            if (name.StartsWith("Xunit.", StringComparison.Ordinal))
                name = name[6..];
            if (name is not ("Fact" or "Theory" or "FactAttribute" or "TheoryAttribute") ||
                attribute.ArgumentList is null)
                continue;
            foreach (var argument in attribute.ArgumentList.Arguments)
            {
                if (argument.NameEquals?.Name.Identifier.ValueText == "Timeout" && StartsWithNumericLiteral(argument.Expression))
                    lines.Add(tree.GetLineSpan(argument.Span).StartLinePosition.Line + 1);
            }
        }
        return lines;
    }

    internal static string? FindMethodBody(string source, string methodName)
    {
        var method = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == methodName);
        return method?.Body?.ToString() ?? method?.ExpressionBody?.ToString();
    }

    internal static IReadOnlyList<string> FindNumericLiteralWaitForExitCalls(string body, string? receiver = null)
    {
        var root = CSharpSyntaxTree.ParseText("class C { void M() { " + body + " } }").GetRoot();
        var calls = new List<string>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var access = invocation.Expression as MemberAccessExpressionSyntax;
            var name = access?.Name.Identifier.ValueText ?? (invocation.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
            if (name == "WaitForExit" && (receiver is null || access?.Expression.ToString() == receiver) &&
                invocation.ArgumentList.Arguments.Count > 0 &&
                StartsWithNumericLiteral(invocation.ArgumentList.Arguments[0].Expression))
                calls.Add(invocation.ToString());
        }
        return calls;
    }

    private static bool StartsWithNumericLiteral(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.Kind() == SyntaxKind.NumericLiteralExpression,
        ParenthesizedExpressionSyntax parentheses => StartsWithNumericLiteral(parentheses.Expression),
        PrefixUnaryExpressionSyntax prefix when prefix.Kind() is SyntaxKind.UnaryPlusExpression or SyntaxKind.UnaryMinusExpression
            => StartsWithNumericLiteral(prefix.Operand),
        CastExpressionSyntax cast => StartsWithNumericLiteral(cast.Expression),
        BinaryExpressionSyntax binary => StartsWithNumericLiteral(binary.Left),
        _ => false
    };

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj")
                continue;
            foreach (var file in EnumerateSourceFiles(child))
                yield return file;
        }
    }
}
