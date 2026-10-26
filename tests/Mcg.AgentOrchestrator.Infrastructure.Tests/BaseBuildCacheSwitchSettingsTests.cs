using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: parses local strings and reads the verified repository manifest without changing shared state.
public sealed class BaseBuildCacheSwitchSettingsTests
{
    [Xunit.Fact]
    public void ParseWithoutEngineDefaultsCacheToOff() =>
        Assert.False(AcceptanceGateEngineSettings.Parse("""{"version":1,"checks":[]}""").BaseBuildCacheEnabled);

    [Xunit.Fact]
    public void ParseWithoutCacheKeyDefaultsCacheToOff() =>
        Assert.False(AcceptanceGateEngineSettings.Parse("""{"version":1,"checks":[],"engine":{}}""").BaseBuildCacheEnabled);

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ParseUsesExplicitCacheSwitch(bool enabled)
    {
        var settings = AcceptanceGateEngineSettings.Parse(
            "{\"version\":1,\"checks\":[],\"engine\":{\"baseBuildCacheEnabled\":" + (enabled ? "true" : "false") + "}}");

        Assert.Equal(enabled, settings.BaseBuildCacheEnabled);
    }

    [Xunit.Fact]
    public void CheckedInManifestDefaultsCacheToOff() =>
        Assert.False(AcceptanceGateEngineSettings.Load(VerifiedRepositoryRoot.Find()).BaseBuildCacheEnabled);
}
