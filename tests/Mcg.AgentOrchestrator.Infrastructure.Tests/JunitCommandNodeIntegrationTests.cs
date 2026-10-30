using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: the real subprocesses, reports and owner artifacts use a unique fixture root.
// Installed node and crosswalk are read-only dependencies; no package installation or global mutation.
public sealed class JunitCommandNodeIntegrationTests : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public async Task RealSuiteWithFailure_PublishesBothRows_AndNamesFailure()
    {
        var node = FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (node is null) Assert.Skip("node is absent from PATH; the real JUnit command integration requires node.");
        // The gate redirects USERPROFILE and the assembly redirects MCG_ORCHESTRATOR_DATA.
        // LOCALAPPDATA is preserved for read-only installed tools by AcceptanceHermeticEnvironment.
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var installedDataRoot = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(localAppData, "Mcg.AgentOrchestrator")
            : OrchestratorDataRoot.ResolveDefaultDirectory();
        var crosswalk = FindOnPath(OperatingSystem.IsWindows() ? "crosswalk.exe" : "crosswalk") ??
            Path.Combine(installedDataRoot, "tools", "crosswalk", "crosswalk.exe");
        Assert.True(File.Exists(crosswalk), $"Required crosswalk parser is missing: {crosswalk}");
        var resolver = new CrosswalkExecutableResolver(crosswalk);
        Assert.NotNull(resolver.Resolve().Identity);
        var root = CreateManifestWorkspace("""
            {"version":1,"checks":[{"name":"declared-test","type":"junit-command","command":"node",
            "arguments":["--test","--test-reporter=junit","--test-reporter-destination=reports/junit.xml"],"reports":["reports/*.xml"]}]}
            """);
        try
        {
            var fixture = Path.Combine(VerifiedRepositoryRoot.Find(), "tests", "junit-command-fixture", "node-test");
            foreach (var template in Directory.EnumerateFiles(fixture, "*.template", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(root, Path.GetRelativePath(fixture, template)[..^".template".Length]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(template, destination);
            }
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.Combine(root, "reports"));
            Assert.False(File.Exists(Path.Combine(root, "package.json")));
            using var fakeCrosswalk = new FakeCrosswalkProcess();
            Assert.IsType<DeclaredProjectDiscoveryAdapter>(ProjectDiscoveryAdapterSelector.Select(root, fakeCrosswalk.Reader));
            var check = new Check { Name = "declared-test", Type = "junit-command", Command = node,
                Arguments = ["--test", "--test-reporter=junit", "--test-reporter-destination=reports/junit.xml"],
                Reports = ["reports/*.xml"], TimeoutMinutes = 3 };
            var verifier = new GoalAcceptanceVerifier(resolver);
            await using var owner = new AcceptanceAttemptExecutionOwner(
                new("real-junit", "goal", root, "tree", "main", "commit",
                    Path.Combine(root, ".orchestrator", "acceptance-gate-attempts", "junit", "attempt-current"), null, Environment.ProcessId, null),
                new AcceptanceGateEngineSettings());
            var result = (await verifier.RunInvocationForOwnerTestsAsync(owner, owner.CreateInvocation(check.Name), check, root, CancellationToken.None)).Result;

            Assert.False(result.Passed);
            Assert.Equal(1, result.ExitCode);
            var rows = JunitCommandFixture.ReadRows(result);
            Assert.Equal(2, rows.Count);
            Assert.Equal(["test/math.test.mjs::adds", "test/strings.test.mjs::intentional failure"], rows.Select(row => row.Id).Order(StringComparer.Ordinal));
            Assert.Equal(TestResultStatus.Passed, Assert.Single(rows, row => row.DisplayName == "adds").Status);
            Assert.Equal(TestResultStatus.Failed, Assert.Single(rows, row => row.DisplayName == "intentional failure").Status);
            Assert.Equal(["test/strings.test.mjs::intentional failure"], result.FailingTestIdentities);
            Assert.Contains("intentional failure", result.OutputTail);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory.Trim('"'), name)).FirstOrDefault(File.Exists);
}
