using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each case owns its filesystem tree and only builds a plan; no commands run.
public sealed class NonHomeProjectGateCheckSelectionTests
{
    private const string CoreSourcePath = "src/Mcg.AgentOrchestrator.Core/Application/Widget.cs";
    private const string ProjectManifest = """
        {
          "version": 1,
          "engine": { "maxConcurrentShards": 1, "enforceStructuralCoverage": false },
          "checks": [
            { "name": "go build", "type": "command", "command": "go", "arguments": ["build", "-buildvcs=false", "./..."] },
            { "name": "go vet", "type": "command", "command": "go", "arguments": ["vet", "./..."] },
            { "name": "go test", "type": "command", "command": "go", "arguments": ["test", "-count=1", "./..."] }
          ],
          "forbiddenChangedPathGlobs": []
        }
        """;

    [Fact]
    public void GoPaths_NonHome_KeepsOnlyManifestChecks() =>
        AssertNonHomePlan(["internal/x/y.go", "internal/x/y_test.go"]);

    [Fact]
    public void CorePath_NonHome_KeepsOnlyManifestChecks() =>
        AssertNonHomePlan([CoreSourcePath]);

    [Fact]
    public void CorePath_Home_RetainsPlannerChecks()
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            Write(root, ".git", "gitdir: isolated-fixture");
            Write(root, CoreSourcePath, "public class Widget { }");
            Write(root, "config/acceptance-manifest.json", """
                {
                  "version": 1,
                  "engine": { "enforceStructuralCoverage": false },
                  "checks": [
                    { "name": "home command", "type": "command", "command": "dotnet", "arguments": ["--info"] }
                  ]
                }
                """);
            Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(root));
            string[] changedFiles = [CoreSourcePath];
            var policy = VerificationPolicyCompiler.Compile(
                AgentRole.Reviewer, string.Empty, string.Empty, null,
                changedFiles, RepositoryTestImpactPlanner.Plan(changedFiles, root));
            var expected = GoalAcceptanceVerifier.MapRequiredPolicyChecksForTests(root, policy)
                .Where(check => !string.Equals(NormalizeProject(check.Project),
                    AcceptancePolicyShardPlanner.InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                root, changedFiles, projectHomeDirectory: null);

            Assert.Contains(expected, check => check.Name == "core tests" &&
                NormalizeProject(check.Project) == "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj");
            foreach (var planned in expected)
            {
                var actual = Assert.Single(checks, check => check.Type == "dotnet-test" &&
                    check.Name == planned.Name && string.Equals(NormalizeProject(check.Project),
                        NormalizeProject(planned.Project), StringComparison.OrdinalIgnoreCase));
                Assert.Equal(planned.Arguments.ToArray(), actual.Arguments.ToArray());
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void AssertNonHomePlan(IReadOnlyList<string> changedFiles)
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var worktree = Path.Combine(root, "worktree");
            var projectHome = Path.Combine(root, "project-home");
            Write(worktree, ".git", "gitdir: isolated-fixture");
            Write(worktree, "go.mod", "module example.com/fixture\n\ngo 1.22\n");
            Write(worktree, "internal/x/y.go", "package x\nfunc Y() int { return 1 }\n");
            Write(worktree, "internal/x/y_test.go", "package x\nimport \"testing\"\nfunc TestY(t *testing.T) { if Y() != 1 { t.Fatal(\"Y\") } }\n");
            Write(projectHome, "acceptance-manifest.json", ProjectManifest);
            Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(worktree));

            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
                worktree, changedFiles, projectHome);

            Assert.Collection(checks,
                check => AssertCommand(check, "go build", ["build", "-buildvcs=false", "./..."]),
                check => AssertCommand(check, "go vet", ["vet", "./..."]),
                check => AssertCommand(check, "go test", ["test", "-count=1", "./..."]));
            Assert.DoesNotContain(checks, check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(checks, ReferencesHomeTestProject);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static void AssertCommand(AcceptanceManifestCheck check, string name, string[] arguments)
    {
        Assert.Equal(name, check.Name);
        Assert.Equal("command", check.Type);
        Assert.Equal("go", check.Command);
        Assert.Equal(arguments, check.Arguments.ToArray());
    }

    private static bool ReferencesHomeTestProject(AcceptanceManifestCheck check) =>
        new[] { check.Name, check.Type, check.Project, check.Command, check.Pattern, check.FilePath, check.Runner }
            .Concat(check.Arguments)
            .Any(value => value?.Replace('\\', '/').Contains(
                "tests/Mcg.AgentOrchestrator.", StringComparison.OrdinalIgnoreCase) == true);

    private static string? NormalizeProject(string? project) => project?.Replace('\\', '/');

    private static void Write(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
