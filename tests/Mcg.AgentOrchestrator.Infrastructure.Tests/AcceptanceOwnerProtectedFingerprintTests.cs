using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class AcceptanceOwnerProtectedFingerprintTests : IDisposable
{
    private const string PolicyPath = "config/conductor-policy.json";
    private const string ManifestPath = "config/acceptance-manifest.json";
    private const string OwnerSourcePath =
        "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceOwnerProtectedFingerprint.cs";
    private const string Trusted = """{"limit":1}""";
    private const string Candidate = """{"limit":2}""";

    // Parallel-safe: each test owns its temporary directory; repository source is read-only.
    private readonly string _worktree = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public void SelectPolicyPathsNormalizesAndExcludesNonPolicyFiles()
    {
        var calls = new List<string[]>();
        string? Resolve(string worktree, string[] arguments)
        {
            Assert.Equal(_worktree, worktree);
            calls.Add(arguments);
            return string.Empty;
        }

        var paths = AcceptanceOwnerProtectedFingerprint.SelectPolicyPaths(_worktree,
            [PolicyPath, "src/a.cs", @"docs\conductor-policy.json"], Resolve, "candidate", "main");

        Assert.Equal([PolicyPath, "docs/conductor-policy.json"], paths);
        Assert.Equal(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("src/a.cs", paths);
        Assert.Equal(["diff", "--name-only", "--no-renames", "main...candidate", "--"], Assert.Single(calls));
    }

    [Fact]
    public void SelectPolicyPathsDeduplicatesNormalizedPathsIgnoringCase()
    {
        var paths = AcceptanceOwnerProtectedFingerprint.SelectPolicyPaths(_worktree,
            [PolicyPath, @"config\conductor-policy.json", "CONFIG/CONDUCTOR-POLICY.JSON"],
            (_, _) => throw new InvalidOperationException("Unexpected git call."), "candidate", "main");

        Assert.Equal(PolicyPath, Assert.Single(paths));
    }

    [Fact]
    public void SelectPolicyPathsRecoversRenameSourceFromNoRenameDiff()
    {
        var calls = new List<string[]>();
        string? Resolve(string worktree, string[] arguments)
        {
            Assert.Equal(_worktree, worktree);
            calls.Add(arguments);
            Assert.Equal(["diff", "--name-only", "--no-renames", "main...candidate", "--"], arguments);
            return "docs/conductor-policy.json\nsrc/renamed.cs\n";
        }

        var paths = AcceptanceOwnerProtectedFingerprint.SelectPolicyPaths(_worktree,
            ["src/renamed.cs"], Resolve, "candidate", "main");

        Assert.Equal("docs/conductor-policy.json", Assert.Single(paths));
        Assert.Single(calls);
    }

    [Fact]
    public void SelectPolicyPathsSkipsDiffForPolicyOnlyChanges()
    {
        var calls = new List<string[]>();
        var paths = AcceptanceOwnerProtectedFingerprint.SelectPolicyPaths(_worktree, [PolicyPath],
            (_, arguments) => { calls.Add(arguments); return string.Empty; }, "candidate", "main");

        Assert.Equal(PolicyPath, Assert.Single(paths));
        Assert.Empty(calls);
    }

    [Fact]
    public void ComputeMatchesClassifierAndChangesWithCandidateContent()
    {
        var calls = new List<string[]>();
        var candidateText = Candidate;
        string? Resolve(string worktree, string[] arguments)
        {
            Assert.Equal(_worktree, worktree);
            calls.Add(arguments);
            return arguments switch
            {
                ["show", "candidate:config/acceptance-manifest.json"] => null,
                ["show", "main:config/conductor-policy.json"] => Trusted,
                ["show", "candidate:config/conductor-policy.json"] => candidateText,
                _ => throw new InvalidOperationException("Unexpected git call: " + string.Join(" ", arguments))
            };
        }

        var actual = AcceptanceOwnerProtectedFingerprint.Compute(_worktree, [PolicyPath],
            Resolve, "candidate", readCommittedCandidate: true, "main");
        var expected = RepositoryChangeClassifier.ComputeOwnerProtectedChangeFingerprint(
            [(PolicyPath, Trusted, Candidate)]);

        Assert.NotNull(expected);
        Assert.Equal(expected, actual);
        Assert.Equal(3, calls.Count);
        Assert.Equal(["show", "candidate:" + ManifestPath], calls[0]);
        Assert.Equal(["show", "main:" + PolicyPath], calls[1]);
        Assert.Equal(["show", "candidate:" + PolicyPath], calls[2]);

        candidateText = """{"limit":3}""";
        var changed = AcceptanceOwnerProtectedFingerprint.Compute(_worktree, [PolicyPath],
            Resolve, "candidate", readCommittedCandidate: true, "main");

        Assert.NotNull(changed);
        Assert.Equal(RepositoryChangeClassifier.ComputeOwnerProtectedChangeFingerprint(
            [(PolicyPath, Trusted, candidateText)]), changed);
        Assert.NotEqual(actual, changed);
    }

    [Fact]
    public void ComputeReturnsNullForUnknownChangesWithoutGitCalls()
    {
        Assert.Null(AcceptanceOwnerProtectedFingerprint.Compute(_worktree, null,
            (_, _) => throw new InvalidOperationException("Unexpected git call."),
            "candidate", readCommittedCandidate: true, "main"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void ComputeReturnsNullForBlankTrustedManifest(string trustedManifest)
    {
        var calls = new List<string[]>();
        string? Resolve(string worktree, string[] arguments)
        {
            Assert.Equal(_worktree, worktree);
            calls.Add(arguments);
            return arguments switch
            {
                ["show", "candidate:config/acceptance-manifest.json"] => Candidate,
                ["show", "main:config/acceptance-manifest.json"] => trustedManifest,
                _ => throw new InvalidOperationException("Unexpected git call: " + string.Join(" ", arguments))
            };
        }

        Assert.Null(AcceptanceOwnerProtectedFingerprint.Compute(_worktree, [ManifestPath],
            Resolve, "candidate", readCommittedCandidate: true, "main"));
        Assert.Equal(2, calls.Count);
        Assert.Equal(["show", "main:" + ManifestPath], calls[1]);
    }

    [Fact]
    public void ComputeReturnsNullWhenExistingCandidateObjectCannotBeRead()
    {
        var calls = new List<string[]>();
        string? Resolve(string worktree, string[] arguments)
        {
            Assert.Equal(_worktree, worktree);
            calls.Add(arguments);
            return arguments switch
            {
                ["show", "candidate:config/acceptance-manifest.json"] => null,
                ["show", "main:config/conductor-policy.json"] => Trusted,
                ["show", "candidate:config/conductor-policy.json"] => null,
                ["cat-file", "-e", "candidate:config/conductor-policy.json"] => string.Empty,
                _ => throw new InvalidOperationException("Unexpected git call: " + string.Join(" ", arguments))
            };
        }

        Assert.Null(AcceptanceOwnerProtectedFingerprint.Compute(_worktree, [PolicyPath],
            Resolve, "candidate", readCommittedCandidate: true, "main"));
        Assert.Equal(4, calls.Count);
        Assert.Equal(["show", "candidate:" + PolicyPath], calls[2]);
        Assert.Equal(["cat-file", "-e", "candidate:" + PolicyPath], calls[3]);
    }

    [Fact]
    public void ComputeRejectsPolicyPathEscapingWorktree()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AcceptanceOwnerProtectedFingerprint.Compute(_worktree, [@"..\..\conductor-policy.json"],
                (_, _) => throw new InvalidOperationException("Unexpected git call."),
                "candidate", readCommittedCandidate: false, "main"));

        Assert.Contains("escapes worktree", exception.Message);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public void ComputeForCandidateRejectsMalformedShaBeforeGitResolution(string candidateSha)
    {
        Assert.False(Directory.Exists(Path.Combine(_worktree, ".git")));
        Assert.Null(AcceptanceOwnerProtectedFingerprint.ComputeForCandidate(_worktree, candidateSha, "main"));

        // The wrapper has no injected resolver: inspect the first statement to prove the early return.
        var source = Path.Combine(VerifiedRepositoryRoot.Find(), OwnerSourcePath);
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(source)).GetRoot();
        var method = Assert.Single(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText == "ComputeForCandidate");
        Assert.NotNull(method.Body);
        var guard = Assert.IsType<IfStatementSyntax>(method.Body.Statements[0]);
        Assert.Equal("!AcceptancePolicyChangeDecision.IsFullSha(candidateSha)", guard.Condition.ToString());
        Assert.Equal("return null;", Assert.IsType<ReturnStatementSyntax>(guard.Statement).ToString());
        Assert.Contains(method.Body.Statements.Skip(1).SelectMany(statement =>
                statement.DescendantNodes().OfType<InvocationExpressionSyntax>()),
            invocation => invocation.Expression.ToString() == "AcceptanceGitTextResolver.Resolve");
    }

    [Fact]
    public void CallersDelegateToNewOwnerWithoutObsoleteVerifierDeclaration()
    {
        var root = VerifiedRepositoryRoot.Find();
        string[] callers =
        [
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.OwnerProtectedConfiguration.cs",
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.OwnerProtectedDecision.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.OwnerReviewHold.cs",
            "src/Mcg.AgentOrchestrator.App/Orchestration/OperatorIntentCoordinator.cs"
        ];
        foreach (var caller in callers)
            Assert.Contains("AcceptanceOwnerProtectedFingerprint.", File.ReadAllText(Path.Combine(root, caller)));

        Assert.False(File.Exists(Path.Combine(root,
            "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.OwnerProtectedFingerprint.cs")));
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Replace('\\', '/').Split('/').Any(segment =>
                segment is "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or "TestResults" or "playwright-report"))
            .Where(file => Path.GetRelativePath(root, file).Replace('\\', '/') != OwnerSourcePath).ToArray();
        Assert.NotEmpty(sourceFiles);
        foreach (var file in sourceFiles)
        {
            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            Assert.DoesNotContain(syntax.DescendantNodes().OfType<MethodDeclarationSyntax>(),
                method => method.Identifier.ValueText == "ComputeOwnerProtectedChangeFingerprintForCandidate");
        }
    }

    [Fact]
    public void OwnerSourceChangeTriggersPostLandingCanary()
    {
        Assert.True(PostLandingCanaryTrigger.Evaluate([OwnerSourcePath]).ShouldRun);
    }

    public void Dispose() => Directory.Delete(_worktree, recursive: true);
}
