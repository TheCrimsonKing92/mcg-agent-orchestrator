using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceTestClassSourceScannerDuplicateConstantTests
{
    [Xunit.Fact]
    public void SameValueDuplicateHelperResolvesCollection()
    {
        using var tree = new TestTree();
        tree.Write("Helper.cs", Helper("SAME"));
        tree.Write("Cli/Helper.cs", Helper("SAME"));
        tree.Write("SampleTests.cs", Consumer("Helper.Name"));

        var descriptor = Xunit.Assert.Single(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Xunit.Assert.Equal("SampleTests", descriptor.FullName);
        Xunit.Assert.Equal("SAME", descriptor.Collection);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Helper.Name")]
    [Xunit.InlineData("Name")]
    public void DifferentValueDuplicateHelperLeavesCollectionUnresolved(string expression)
    {
        using var tree = new TestTree();
        tree.Write("Helper.cs", Helper("FIRST"));
        tree.Write("Cli/Helper.cs", Helper("SECOND"));
        tree.Write("SampleTests.cs", Consumer(expression));

        var descriptor = Xunit.Assert.Single(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Xunit.Assert.Equal("SampleTests", descriptor.FullName);
        Xunit.Assert.Null(descriptor.Collection);
    }

    [Xunit.Fact]
    public void DuplicateHelperKeepsLaneMembershipAndInventory()
    {
        using var tree = new TestTree();
        tree.Write("Helper.cs", Helper("SAME"));
        tree.Write("SampleTests.cs", Consumer("Helper.Name"));

        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot())
            .InfrastructureTestLanes.Select(lane => lane with
            {
                OwnedCollections = lane.Name == "Process spawning" ? ["SAME"] : []
            }).ToArray();
        var singleLanes = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, tree.Root);
        var singleInventory = AcceptanceTestInventorySource.Read(tree.Root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, EmptyMainMetadata);

        tree.Write("Cli/Helper.cs", Helper("SAME"));
        var duplicateLanes = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, tree.Root);
        var duplicateInventory = AcceptanceTestInventorySource.Read(tree.Root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, EmptyMainMetadata);

        Xunit.Assert.Equal("Process spawning", Xunit.Assert.Single(
            AcceptanceLaneMembership.LanesIncluding(singleLanes, "SampleTests")).Name);
        Xunit.Assert.Equal(singleLanes.Select(lane => (lane.Name, lane.Filter)),
            duplicateLanes.Select(lane => (lane.Name, lane.Filter)));
        Xunit.Assert.Equal(singleInventory.Classes, duplicateInventory.Classes);
        Xunit.Assert.Equal(singleInventory.DisabledCollections.Order(StringComparer.Ordinal),
            duplicateInventory.DisabledCollections.Order(StringComparer.Ordinal));
        Xunit.Assert.Equal(singleInventory.ProcessLocalCollectionsAtMain.Order(StringComparer.Ordinal),
            duplicateInventory.ProcessLocalCollectionsAtMain.Order(StringComparer.Ordinal));
    }

    [Xunit.Fact]
    public void DifferentOwnerNamesKeepExistingUnqualifiedBehavior()
    {
        using var tree = new TestTree();
        tree.Write("Alpha.cs", "internal static class Alpha { internal const string Name = \"FIRST\"; }");
        tree.Write("Beta.cs", "internal static class Beta { internal const string Name = \"SECOND\"; }");
        tree.Write("SampleTests.cs", Consumer("Name"));

        Xunit.Assert.Throws<InvalidDataException>(() => AcceptanceTestClassSourceScanner.Scan(tree.Root));
    }

    private static string Helper(string value) =>
        $"internal static class Helper {{ internal const string Name = \"{value}\"; }}";

    private static string Consumer(string expression) =>
        $"[Xunit.Collection({expression})] public class SampleTests {{ [Xunit.Fact] public void Runs() {{ }} }}";

    private static string? EmptyMainMetadata(string _, string[] args) => args[0] == "grep" ? "" : null;

    private sealed class TestTree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "acceptance-scanner-" + Guid.NewGuid().ToString("N"));

        internal TestTree()
        {
            Directory.CreateDirectory(Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests"));
            Directory.CreateDirectory(Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.TestSupport"));
        }

        internal void Write(string relativePath, string source)
        {
            var path = Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
