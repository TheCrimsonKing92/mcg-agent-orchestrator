using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: source inspection is read-only and uses the worktree under verification.
public sealed class PreTesterAlwaysRunGuardTestClassesTests
{
    [Fact]
    public void EveryListedGuardHasExactlyOneTestSourceDeclarationAndReason()
    {
        var root = VerifiedRepositoryRoot.Find();
        var entries = PreTesterAlwaysRunGuardTestClasses.Entries;
        Assert.Equal(9, entries.Count);
        Assert.Equal(entries.Count, entries.Select(entry => entry.TestClass).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            var paths = AcceptanceTestSourceResolver.ResolveSourcePaths(root, null, entry.TestClass + ".Fact")
                .Where(path => path.StartsWith("tests/", StringComparison.Ordinal)).ToArray();
            Assert.Equal("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + entry.TestClass + ".cs",
                Assert.Single(paths));
            var resolved = DeveloperDeferredTestSelections.ResolveNames(root, [entry.TestClass], requireUniqueSourceFile: true);
            Assert.Empty(resolved.NotRun);
            var selection = Assert.Single(resolved.Selections);
            Assert.Equal("Mcg.AgentOrchestrator.Infrastructure.Tests", selection.TestProject);
            Assert.Equal(entry.TestClass, selection.TestClass);
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
