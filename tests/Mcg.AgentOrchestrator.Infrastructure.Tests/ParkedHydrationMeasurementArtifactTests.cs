using System.Text.Json;

public sealed class ParkedHydrationMeasurementArtifactTests
{
    [Fact]
    public void WritesUnderEachOwnedRoot()
    {
        var firstRoot = CreateOwnedRoot();
        var secondRoot = CreateOwnedRoot();
        try
        {
            var firstPath = ParkedHydrationMeasurementArtifact.Write(firstRoot, 53, 1000, 200, 0.8, 3000, 2500);
            var secondPath = ParkedHydrationMeasurementArtifact.Write(secondRoot, 7, 500, 100, 0.8, 1500, 1250);

            Assert.NotEqual(firstPath, secondPath);
            Assert.Equal(Path.Combine(firstRoot, "parked-hydration-measurement.json"), firstPath);
            Assert.Equal(Path.Combine(secondRoot, "parked-hydration-measurement.json"), secondPath);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            using var firstArtifact = JsonDocument.Parse(File.ReadAllText(firstPath));
            using var secondArtifact = JsonDocument.Parse(File.ReadAllText(secondPath));
            Assert.Equal("conduct-loop-parked-hydration", firstArtifact.RootElement.GetProperty("fixture").GetString());
            Assert.Equal("conduct-loop-parked-hydration", secondArtifact.RootElement.GetProperty("fixture").GetString());
            Assert.Equal(53, firstArtifact.RootElement.GetProperty("parked_goal_count").GetInt32());
            Assert.Equal(7, secondArtifact.RootElement.GetProperty("parked_goal_count").GetInt32());
        }
        finally
        {
            DeleteOwnedRoot(firstRoot);
            DeleteOwnedRoot(secondRoot);
        }
    }

    [Fact]
    public void WriteLeavesSharedLegacyFileUntouched()
    {
        var ownedRoot = CreateOwnedRoot();
        var sharedPath = Path.Combine(Path.GetTempPath(), "mcg-conduct-loop-parked-hydration-measurement-latest.json");
        var sharedExisted = File.Exists(sharedPath);
        var sharedContents = sharedExisted ? File.ReadAllBytes(sharedPath) : null;
        var sharedWriteTime = sharedExisted ? File.GetLastWriteTimeUtc(sharedPath) : default;
        try
        {
            ParkedHydrationMeasurementArtifact.Write(ownedRoot, 53, 1000, 200, 0.8, 3000, 2500);

            Assert.Equal(sharedExisted, File.Exists(sharedPath));
            if (sharedExisted)
            {
                Assert.Equal(sharedContents, File.ReadAllBytes(sharedPath));
                Assert.Equal(sharedWriteTime, File.GetLastWriteTimeUtc(sharedPath));
            }
        }
        finally
        {
            DeleteOwnedRoot(ownedRoot);
        }
    }

    [Fact]
    public void HydrationTestSourceNoLongerUsesSharedLatestFile()
    {
        var sourcePath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "CliCommandTestsPersistentRunnerCommandsConductLoopHydration.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("parked-hydration-measurement-latest.json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetTempPath(), \"mcg-conduct-loop", source, StringComparison.Ordinal);
    }

    private static string CreateOwnedRoot() =>
        Path.Combine(Path.GetTempPath(), nameof(ParkedHydrationMeasurementArtifactTests), Guid.NewGuid().ToString("n"));

    private static void DeleteOwnedRoot(string ownedRoot)
    {
        try
        {
            Directory.Delete(ownedRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
