using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its filesystem tree and only builds a plan; no commands run.
public sealed class ForeignLayoutAcceptancePlanTests
{
    [Theory]
    [InlineData("Foreign.sln")]
    [InlineData("Foreign.slnx")]
    public void ForeignSourceChange_EffectivePlanKeepsOnlyDeclaredTestProject(string solution)
    {
        const string sourcePath = "src/Foreign.Domain/Widget.cs";
        const string testProject = "tests/Foreign.Specs/Foreign.Specs.csproj";
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            Write(root, ".git", "gitdir: isolated-fixture");
            Write(root, solution, "");
            Write(root, "src/Foreign.Domain/Foreign.Domain.csproj", "<Project />");
            Write(root, sourcePath, "namespace Foreign.Domain; public class Widget { }");
            Write(root, testProject, "<Project />");
            Write(root, "config/acceptance-manifest.json", """
                {
                  "engine": { "enforceStructuralCoverage": false },
                  "checks": [
                    { "name": "foreign specs", "type": "dotnet-test",
                      "project": "tests/Foreign.Specs/Foreign.Specs.csproj" }
                  ]
                }
                """);

            var tree = CandidateTreeProbe.ForRepositoryRoot(root);
            Assert.NotSame(CandidateTreeProbe.AssumeAllPresent, tree);
            Assert.True(tree.Exists(testProject));
            Assert.False(tree.Exists("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"));

            Write(root, sourcePath, "namespace Foreign.Domain; public class Widget { public int Value => 1; }");
            var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(root, [sourcePath]);

            var declared = Assert.Single(checks, check => check.Type == "dotnet-test");
            Assert.Equal("foreign specs", declared.Name);
            Assert.Equal(testProject, declared.Project);
            Assert.DoesNotContain(checks, check => ReferencesHomeTestProject(check));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static bool ReferencesHomeTestProject(GoalAcceptanceVerifier.AcceptanceManifestCheck check) =>
        new[] { check.Project, check.Command }.Concat(check.Arguments)
            .Any(value => value?.Replace('\\', '/').Contains(
                "tests/Mcg.AgentOrchestrator.", StringComparison.OrdinalIgnoreCase) == true);

    private static void Write(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
