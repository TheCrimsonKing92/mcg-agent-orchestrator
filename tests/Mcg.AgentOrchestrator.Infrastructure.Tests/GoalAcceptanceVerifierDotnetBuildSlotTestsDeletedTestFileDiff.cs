using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses the build-slot lane's collection because preparation owns isolated build leases.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsDeletedTestFileDiff : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public async Task Preparation_TwoProjects_ResolvesRemovalInputOncePerPass()
    {
        var candidate = CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": { "maxConcurrentShards": 1, "enforceStructuralCoverage": true },
              "checks": [
                { "name": "sample tests", "type": "dotnet-test", "project": "tests/Sample.Tests/Sample.Tests.csproj", "arguments": [] },
                { "name": "second tests", "type": "dotnet-test", "project": "tests/Second.Tests/Second.Tests.csproj", "arguments": [] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var main = Path.Combine(Path.GetTempPath(), "mcg-coverage-main", Guid.NewGuid().ToString("N"));
        var goal = GoalId.New();
        var resolutions = 0;
        var candidateDiscoveries = new List<string>();
        TestOverrides.ResolveMainWorktreePathForTests = _ => main;
        TestOverrides.ResolveDeletedTestFilesForTests = root =>
        {
            Assert.Equal(candidate, root);
            resolutions++;
            return [];
        };
        try
        {
            foreach (var root in new[] { candidate, main })
            foreach (var name in new[] { "Sample.Tests", "Second.Tests" })
            {
                var project = Path.Combine(root, "tests", name, name + ".csproj");
                Directory.CreateDirectory(Path.GetDirectoryName(project)!);
                File.WriteAllText(project,
                    "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
            }
            var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, root, _) =>
            {
                if (args.Contains("--list-tests"))
                {
                    if (root == candidate)
                        candidateDiscoveries.Add(Assert.Single(args.Where(arg => arg.EndsWith(".csproj"))));
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0,
                        "DISCOVERED_TEST: ExampleTests.Runs"));
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            });

            for (var pass = 1; pass <= 2; pass++)
            {
                // Completed lane receipts are irrelevant to preparation; its discoveries prove both projects ran.
                await verifier.RunStructuralCoverageForTests(candidate, goal, ["src/Sample.cs"], false, []);
                Assert.Equal(pass, resolutions);
                Assert.Equal(pass * 2, candidateDiscoveries.Count);
                Assert.Equal(2, candidateDiscoveries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }
        finally
        {
            TestOverrides.ResolveMainWorktreePathForTests = null;
            TestOverrides.ResolveDeletedTestFilesForTests = null;
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal);
            Directory.Delete(candidate, recursive: true);
            if (Directory.Exists(main))
                Directory.Delete(main, recursive: true);
        }
    }
}
