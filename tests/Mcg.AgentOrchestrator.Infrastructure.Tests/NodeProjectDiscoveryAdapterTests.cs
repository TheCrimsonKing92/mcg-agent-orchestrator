using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: fixtures own their directories; discovery invokes no processes or shared state.
public sealed class NodeProjectDiscoveryAdapterTests
{
    [Fact]
    public void Discover_NpmVitestTypeScript_RecordsConfidentSourcedFacts()
    {
        using var fixture = new Fixture();

        var model = fixture.Discover();

        Assert.Equal(ProjectStack.Node, model.Stack);
        Assert.Equal(".", model.RepositoryRoot);
        AssertFact(model, "node-package-manager", "npm", "package-lock.json", 1);
        AssertFact(model, "node-test-framework", "vitest", "package.json", 9);
        AssertFact(model, "node-typescript-config", "tsconfig.json", "tsconfig.json", 1);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey is
            "environment/node-package-manager" or "environment/node-test-framework");
    }

    [Theory]
    [InlineData("{\"devDependencies\":{\"jest\":\"30\"}}", "jest")]
    [InlineData("{}", "undetermined")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\",\"jest\":\"30\"}}", "ambiguous")]
    [InlineData("{\"dependencies\":{\"vitest\":\"4\"}}", "undetermined")]
    [InlineData("{\"scripts\":{\"test\":\"vitest run\"}}", "undetermined")]
    public void Discover_UnsupportedFramework_AsksWithoutGuessing(string package, string expected)
    {
        using var fixture = new Fixture(package);

        var model = fixture.Discover();

        var fact = Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-test-framework").Value;
        Assert.Equal(expected, fact.Value);
        Assert.Equal(expected == "jest" ? FactConfidence.High : FactConfidence.Low, fact.Confidence);
        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == "environment/node-test-framework");
        Assert.Equal("package.json", question.Source.Path);
        Assert.DoesNotContain(model.EnvironmentNeeds, need => need.Name == "node-test-framework" && need.Value.Value == "vitest");
    }

    [Theory]
    [InlineData("", "undetermined")]
    [InlineData("package-lock.json|pnpm-lock.yaml", "ambiguous")]
    [InlineData("pnpm-lock.yaml|yarn.lock", "ambiguous")]
    [InlineData("pnpm-lock.yaml", "pnpm")]
    [InlineData("yarn.lock", "yarn")]
    public void Discover_UnsupportedLockfiles_AsksWithoutGuessing(string files, string expected)
    {
        using var fixture = new Fixture(lockfiles: files.Split('|', StringSplitOptions.RemoveEmptyEntries));
        fixture.Write("package.json", """{"packageManager":"npm@11","devDependencies":{"vitest":"4"}}""");

        var model = fixture.Discover();

        var fact = Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-package-manager").Value;
        Assert.Equal(expected, fact.Value);
        Assert.Equal(expected is "pnpm" or "yarn" ? FactConfidence.High : FactConfidence.Low, fact.Confidence);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "environment/node-package-manager");
        Assert.NotEqual("npm", fact.Value);
        if (expected is "pnpm" or "yarn")
        {
            Assert.Equal(files, fact.Source.Path);
            Assert.Equal(1, fact.Source.Line);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discover_AnyCheckout_RecordsRegistryHazardAndEnvironmentQuestion(bool installed)
    {
        using var fixture = new Fixture();
        if (installed)
            fixture.Write("node_modules/installed.txt", "already installed");

        var model = fixture.Discover();

        var hazard = Assert.Single(model.Hazards);
        Assert.Equal("node-install-needs-registry-network", hazard.Name);
        Assert.Equal(".", hazard.UnitId);
        Assert.Equal("npm-registry", hazard.IsolationKey.Value);
        Assert.Equal(FactConfidence.High, hazard.IsolationKey.Confidence);
        Assert.Equal("package-lock.json", hazard.IsolationKey.Source.Path);
        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == "environment/node-registry-access");
        Assert.Contains("worker and gate", question.Question, StringComparison.Ordinal);
        Assert.Contains("configured npm registry", question.Question, StringComparison.Ordinal);
        Assert.Contains("no node_modules", question.Question, StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_NoTypeScriptConfig_RecordsAbsenceWithoutQuestion()
    {
        using var fixture = new Fixture(hasTsconfig: false);

        var model = fixture.Discover();

        AssertFact(model, "node-typescript-config",
            "absent: no TypeScript type-check, because tsconfig.json is absent at the root", "tsconfig.json", 1);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.Contains("typescript", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("[]")]
    [InlineData("{\"devDependencies\":[]}")]
    public void Discover_MalformedPackage_ReportsPathAndLeavesFrameworkUnresolved(string package)
    {
        using var fixture = new Fixture(package);

        var model = fixture.Discover();

        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == "discovery/files/package.json/parse");
        Assert.Equal("package.json", question.Source.Path);
        Assert.Equal("undetermined", Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-test-framework").Value.Value);
        Assert.Empty(NodeVerificationChecks.For(model).Checks);
    }

    [Fact]
    public void Discover_UnreadablePackage_ReportsReadQuestion()
    {
        using var fixture = new Fixture();
        using var locked = new FileStream(Path.Combine(fixture.Root, "package.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var model = fixture.Discover();

        Assert.Contains(model.OwnerQuestions, item => item.FactKey == "discovery/files/package.json/read" && item.Source.Path == "package.json");
        Assert.Empty(NodeVerificationChecks.For(model).Checks);
    }

    [Fact]
    public void Discover_FileOnlyContract_DoesNotMeasureAndReadsIntegrationReference()
    {
        using var fixture = new Fixture();
        fixture.Write(".git/refs/remotes/origin/HEAD", "ref: refs/remotes/origin/main\n");

        var model = new NodeProjectDiscoveryAdapter().Discover(fixture.Root, measurer: new RejectingMeasurer());

        Assert.Equal("main", model.IntegrationBranch?.Value);
        Assert.Equal(FactConfidence.High, model.IntegrationBranch?.Confidence);
        Assert.Empty(model.Commands);
        Assert.Empty(model.Measurements);
        Assert.Empty(model.Units);
        Assert.Empty(model.TestSetups);
        Assert.Empty(model.Dependencies);
    }

    private static void AssertFact(ProjectModel model, string name, string value, string path, int line)
    {
        var fact = Assert.Single(model.EnvironmentNeeds, need => need.Name == name).Value;
        Assert.Equal(value, fact.Value);
        Assert.Equal(FactConfidence.High, fact.Confidence);
        Assert.Equal(path, fact.Source.Path);
        Assert.Equal(line, fact.Source.Line);
    }

    private sealed class RejectingMeasurer : IUnitCommandMeasurer
    {
        public UnitCommandMeasurementResult Measure(string repositoryRoot, string unitId, UnitCommandKinds kind, string command) =>
            throw new InvalidOperationException("Node discovery must never measure or execute commands.");
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("node-discovery-");
        internal string Root => _root.FullName;

        internal Fixture(string package = DefaultPackage, bool hasTsconfig = true, IReadOnlyList<string>? lockfiles = null)
        {
            Write("package.json", package);
            foreach (var file in lockfiles ?? ["package-lock.json"])
                Write(file, "{}");
            if (hasTsconfig)
                Write("tsconfig.json", "existence alone establishes type-check capability");
            Write("vitest.config.ts", "export default {};");
            Write("vite.config.ts", "export default {};");
        }

        internal void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        internal ProjectModel Discover() => new NodeProjectDiscoveryAdapter().Discover(Root);

        internal static OwnerAnswer[] Answers() =>
        [
            new("engine/timeouts/defaultMinutes", "5", "fixture", DateTimeOffset.UnixEpoch),
            new("engine/timeouts/buildServerShutdownMinutes", "3", "fixture", DateTimeOffset.UnixEpoch),
            new("engine/timeouts/discoveryMinutes", "4", "fixture", DateTimeOffset.UnixEpoch)
        ];

        public void Dispose() => _root.Delete(recursive: true);

        private const string DefaultPackage = """
            {
              "type": "module",
              "private": true,
              "scripts": {
                "test": "node --test && browser-performance && vitest run",
                "build": "tsc --noEmit && vite build"
              },
              "devDependencies": {
                "vitest": "4.1.10",
                "typescript": "^5.0.0"
              }
            }
            """;
    }
}
