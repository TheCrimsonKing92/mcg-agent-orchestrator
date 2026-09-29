using System.Text.Json;

public sealed class ReceiptHitMeasurementArtifactTests
{
    [Xunit.Fact]
    public void MeasurementArtifactStaysUnderOwnedRoot()
    {
        var ownedRoot = Path.Combine(Path.GetTempPath(), nameof(ReceiptHitMeasurementArtifactTests), Guid.NewGuid().ToString("n"));
        var sharedFallback = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-receipt-hit-measurement-latest.json");
        var fallbackExisted = File.Exists(sharedFallback);
        var fallbackWriteTime = fallbackExisted ? File.GetLastWriteTimeUtc(sharedFallback) : default;
        var fallbackContents = fallbackExisted ? File.ReadAllBytes(sharedFallback) : null;
        Directory.CreateDirectory(ownedRoot);

        try
        {
            var events = Array.Empty<DispatchProcessHostTests.SandboxPrepEvent>();
            var warmup = new DispatchProcessHostTests.MeasuredDispatch("complete", 1, 2, false, "", events);
            var hit = new DispatchProcessHostTests.MeasuredDispatch("receipt-hit", 1, 2, true, "", events);

            var artifactPath = DispatchProcessHostTests.WriteReceiptHitMeasurementArtifact(
                ownedRoot, warmup, [hit, hit]);

            Assert.Equal(Path.Combine(ownedRoot, "receipt-hit-measurement.json"), artifactPath);
            Assert.True(File.Exists(artifactPath));
            using var artifact = JsonDocument.Parse(File.ReadAllText(artifactPath));
            Assert.Equal(2, artifact.RootElement.GetProperty("receiptHits").GetArrayLength());
            Assert.Equal(fallbackExisted, File.Exists(sharedFallback));
            if (fallbackExisted)
            {
                Assert.Equal(fallbackWriteTime, File.GetLastWriteTimeUtc(sharedFallback));
                Assert.Equal(fallbackContents, File.ReadAllBytes(sharedFallback));
            }
        }
        finally
        {
            Directory.Delete(ownedRoot, recursive: true);
        }
    }

    [Xunit.Fact]
    public void MeasurementWriterDoesNotReadExternalPathOrUseSharedFallback()
    {
        var sourcePath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "DispatchProcessHostTests.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("MCG_RECEIPT_HIT_MEASUREMENT_PATH", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mcg-dispatch-host-receipt-hit-measurement-latest.json", source, StringComparison.Ordinal);
    }
}
