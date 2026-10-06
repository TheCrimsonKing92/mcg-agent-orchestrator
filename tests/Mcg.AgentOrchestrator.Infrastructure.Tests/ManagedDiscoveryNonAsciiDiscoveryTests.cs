[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ManagedDiscoveryNonAsciiDiscoveryTests
{
    [Xunit.Fact]
    public void Discovery_PreservesNonAsciiTheoryDisplayName()
    {
        var catalog = MtpTestRunnerScriptTests.DiscoverManagedTestCatalog(
            allowEmpty: false, "--filter-class", "*ManagedDiscoveryNonAsciiRowTests*");

        var descriptor = Assert.Single(catalog);
        var units = string.Join(" ", descriptor.DisplayName.Select(c => $"U+{(int)c:X4}"));
        var diagnostic = $"DisplayName units: {units}; parent stdout code page: {Console.OutputEncoding.CodePage}";
        Assert.EndsWith(nameof(ManagedDiscoveryNonAsciiRowTests), descriptor.TypeName, StringComparison.Ordinal);
        Assert.True(descriptor.DisplayName.Contains('\u2022'), diagnostic);
        Assert.True(descriptor.DisplayName.Contains("\\t", StringComparison.Ordinal), diagnostic);
        Assert.False(descriptor.DisplayName.Contains('\u0007'), diagnostic);
    }
}
