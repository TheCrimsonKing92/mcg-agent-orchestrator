public sealed class AssemblyTempRedirectTests
{
    [Xunit.Fact]
    public void SkipsCandidateThatCannotCreateFiles()
    {
        var attempted = new List<string>();

        var selected = AssemblyTempRedirect.SelectWritableRoot(
            ["existing-but-denied", "writable-fallback"],
            candidate =>
            {
                attempted.Add(candidate);
                return candidate == "writable-fallback";
            });

        Xunit.Assert.Equal("writable-fallback", selected);
        Xunit.Assert.Equal(["existing-but-denied", "writable-fallback"], attempted);
    }

    [Xunit.Fact]
    public void ReturnsNullWhenNoCandidateIsWritable()
    {
        var selected = AssemblyTempRedirect.SelectWritableRoot(
            ["denied-primary", "denied-fallback"],
            _ => false);

        Xunit.Assert.Null(selected);
    }
}
