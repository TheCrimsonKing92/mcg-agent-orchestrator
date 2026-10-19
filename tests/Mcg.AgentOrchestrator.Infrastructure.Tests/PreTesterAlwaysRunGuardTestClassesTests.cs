using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repository inspection is read-only; the fixture owns its temporary directory.
public sealed class PreTesterAlwaysRunGuardTestClassesTests
{
    [Fact]
    public void EveryListedGuardHasExactlyOneTestSourceDeclarationAndReason()
    {
        var root = VerifiedRepositoryRoot.Find();
        var entries = PreTesterAlwaysRunGuardTestClasses.Entries;
        Assert.Equal(9, entries.Count);
        Assert.Equal(entries.Count, entries.Select(entry => entry.TestClass).Distinct(StringComparer.Ordinal).Count());
        var sources = new AcceptanceTestSourceResolver.Request(root);
        var resolved = DeveloperDeferredTestSelections.ResolveNames(root,
            entries.Select(entry => entry.TestClass).ToArray(), requireUniqueSourceFile: true);
        Assert.Empty(resolved.NotRun);
        Assert.Equal(entries.Count, resolved.Selections.Count);
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            var paths = sources.ResolveSourcePaths(null, entry.TestClass + ".Fact")
                .Where(path => path.StartsWith("tests/", StringComparison.Ordinal)).ToArray();
            Assert.Equal("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + entry.TestClass + ".cs",
                Assert.Single(paths));
            var selection = Assert.Single(resolved.Selections, selection => selection.TestClass == entry.TestClass);
            Assert.Equal("Mcg.AgentOrchestrator.Infrastructure.Tests", selection.TestProject);
            Assert.Equal(entry.TestClass, selection.TestClass);
        }
        Assert.Equal(2, sources.EnumerationCount);
    }

    [Fact]
    public void SharedRequest_MatchesFreshRequests_AndStillReportsDuplicateDeclarations()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var project = Path.Combine(root, "tests", "P");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "P.csproj"), "<Project />");
            string[] classes = ["Alpha", "Beta", "Gamma", "Delta"];
            foreach (var name in classes)
                File.WriteAllText(Path.Combine(project, name + ".cs"), $"namespace N; partial class {name} {{ }}");
            File.WriteAllText(Path.Combine(project, "DeltaPartial.cs"), "namespace N; partial class Delta { }");

            var shared = new AcceptanceTestSourceResolver.Request(root);
            foreach (var name in classes)
            {
                var identity = "N." + name + ".Fact";
                var freshPaths = new AcceptanceTestSourceResolver.Request(root).ResolveSourcePaths(null, identity);
                var sharedPaths = shared.ResolveSourcePaths(null, identity);
                Assert.Equal(freshPaths, sharedPaths);
                if (name == "Delta")
                {
                    Assert.Equal(2, freshPaths.Count);
                    Assert.Equal(2, sharedPaths.Count);
                }
                else
                {
                    Assert.Single(freshPaths);
                    Assert.Single(sharedPaths);
                }
            }
            Assert.Equal(1, shared.EnumerationCount);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void MembershipUsesTheDeclaringClassRatherThanIdentitySubstrings()
    {
        foreach (var entry in PreTesterAlwaysRunGuardTestClasses.Entries)
        {
            Assert.True(PreTesterAlwaysRunGuardTestClasses.IsListed(entry.TestClass + ".Fact"));
            Assert.True(PreTesterAlwaysRunGuardTestClasses.IsListed("Ns." + entry.TestClass + ".Theory(value: 1)"));
            Assert.False(PreTesterAlwaysRunGuardTestClasses.IsListed("UnlistedTests." + entry.TestClass));
            Assert.False(PreTesterAlwaysRunGuardTestClasses.IsListed(entry.TestClass + "Extra.Fact"));
            Assert.False(PreTesterAlwaysRunGuardTestClasses.IsListed(entry.TestClass));
        }
    }
}
