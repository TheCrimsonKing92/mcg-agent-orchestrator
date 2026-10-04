using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure string functions; no shared state or I/O.
public sealed class AcceptanceTrxOutcomeTaxonomyFirstNonEmptyLineTests
{
    [Xunit.Fact]
    public void TaxonomyAndForwarderReturnSameFirstNonEmptyLine()
    {
        (string? Input, string? Expected)[] cases =
        [
            (null, null),
            ("", null),
            ("  \t ", null),
            ("single", "single"),
            ("\n  \nsecond", "second"),
            ("first\r\nsecond", "first"),
            ("\n   padded line", "padded line")
        ];

        foreach (var (input, expected) in cases)
        {
            var taxonomyResult = AcceptanceTrxOutcomeTaxonomy.FirstNonEmptyLine(input);
            var verifierResult = GoalAcceptanceVerifier.FirstNonEmptyLine(input);
            Assert.Equal(expected, taxonomyResult);
            Assert.Equal(expected, verifierResult);
            Assert.Equal(verifierResult, taxonomyResult);
        }
    }
}
