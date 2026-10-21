using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerRecursivePathTests
{
    [Fact]
    public void RecursivePathsPrefixShortRootsAndPreserveOtherPathForms()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows native paths required.");
        Assert.Equal(@"C:\short", IcaclsIntegrityLabeler.NativeFilePath(@"C:\short"));
        Assert.Equal(@"\\?\C:\short\", IcaclsIntegrityLabeler.NativeFilePath(@"C:\short\", recursive: true));
        Assert.Equal(@"\\?\UNC\server\share\short\", IcaclsIntegrityLabeler.NativeFilePath(@"\\server\share\short\", recursive: true));
        Assert.Equal(@"\\?\C:\short", IcaclsIntegrityLabeler.NativeFilePath(@"\\?\C:\short", recursive: true));
        Assert.Equal(@"\\.\C:\short", IcaclsIntegrityLabeler.NativeFilePath(@"\\.\C:\short", recursive: true));
        Assert.Equal(@"\\?\" + Path.GetFullPath("relative-root"), IcaclsIntegrityLabeler.NativeFilePath("relative-root", recursive: true));
    }

    [Fact]
    public void RealRecursiveLabel_ReachesLongDescendantThatLegacyRootCannotProcess()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Actual Windows icacls execution required.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcg-il-tree-" + Guid.NewGuid().ToString("N")));
        Assert.True(root.Length < 260, "The fixture must start at a short root.");
        var leaf = Path.Combine(root, new string('d', 130), new string('f', 130) + ".cache");
        Assert.True(leaf.Length > 260);
        Directory.CreateDirectory(Path.GetDirectoryName(leaf)!);
        File.WriteAllText(leaf, "owned recursive path control");
        try
        {
            var legacy = new IcaclsIntegrityLabeler(info =>
            {
                // Revert only the production path argument; execute the actual same child command.
                info.ArgumentList[0] = root;
                return Process.Start(info);
            }).SetIntegrityWithDiagnostics(root, "(OI)(CI)L", recursive: true);
            Assert.False(legacy.Succeeded);
            using var diagnostic = JsonDocument.Parse(legacy.Diagnostic);
            Assert.Equal(3, diagnostic.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(leaf, diagnostic.RootElement.GetProperty("stderrPrefix").GetString());

            var labeler = new IcaclsIntegrityLabeler();
            var result = labeler.SetIntegrityWithDiagnostics(root, "(OI)(CI)L", recursive: true);
            Assert.True(result.Succeeded, result.Diagnostic);
            var label = labeler.Query(leaf);
            Assert.True(label.Exists);
            Assert.True(label.Low);
            Assert.Null(label.NativeQueryError);
            Assert.Equal("owned recursive path control", File.ReadAllText(leaf));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
