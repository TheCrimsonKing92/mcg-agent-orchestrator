// Parallel-safe: read-only inspection of the explicitly verified candidate repository.
public sealed class ProjectDiscoveryAdapterSelectorSourceTests
{
    [Fact]
    public void Selector_DelegatesStackKnowledge_WithoutCopiedDetectionRules()
    {
        var source = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.Infrastructure", "Onboarding", "ProjectDiscoveryAdapterSelector.cs"));

        Assert.DoesNotContain("DetectLua", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceExtensions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lua go js jsx ts tsx", source, StringComparison.Ordinal);
        Assert.DoesNotContain("detect.go", source, StringComparison.Ordinal);
    }
}
