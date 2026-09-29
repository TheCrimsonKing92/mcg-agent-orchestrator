public sealed class MtpFilterArgumentSemanticsTests
{
    [Xunit.Fact]
    public void UnknownArgumentKindFailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MtpFilterArgumentSemantics.KindSignature(["--filter-trait", "Category=CrossTick"]));
        Assert.Contains("--filter-trait", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OddLengthArgumentVectorFailsLoudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MtpFilterArgumentSemantics.KindSignature(["--filter-class"]));
        Assert.Contains("option/value pairs", error.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MissingRealRunnerSignatureIsReported()
    {
        Assert.Equal(["--filter-method"], MtpFilterArgumentSemantics.UncoveredSignatures(
            ["--filter-class", "--filter-method"], ["--filter-class"]));
    }
}
