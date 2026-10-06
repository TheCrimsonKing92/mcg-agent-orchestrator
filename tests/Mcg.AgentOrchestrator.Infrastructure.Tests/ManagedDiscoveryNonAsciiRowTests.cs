public sealed class ManagedDiscoveryNonAsciiRowTests
{
    [Xunit.Theory]
    [Xunit.InlineData("a•b\tc")]
    public void Row(string value)
    {
        Assert.Equal("a•b\tc", value);
    }
}
