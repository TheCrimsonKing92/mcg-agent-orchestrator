using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneNotExecutedDecisionTests
{
    [Xunit.Fact]
    public void AllDeclared_NormalizedDataCasesAccept()
    {
        var result = RemoteLaneNotExecutedDecision.Decide(2,
            new(["Lane0Tests.Skipped(1)", "Lane0Tests.Skipped(2)"], false),
            new HashSet<string>(StringComparer.Ordinal) { "Lane0Tests.Skipped" });

        Assert.True(result.Accept);
        Assert.Null(result.FirstUndeclared);
    }

    [Xunit.Fact]
    public void UndeclaredIdentity_FallsBackWithFirstOrdinalIdentity()
    {
        var result = RemoteLaneNotExecutedDecision.Decide(3,
            new(["Lane2Tests.Skipped", "Lane0Tests.Skipped", "Lane1Tests.Skipped"], false),
            new HashSet<string>(StringComparer.Ordinal) { "Lane0Tests.Skipped" });

        Assert.False(result.Accept);
        Assert.Equal("Lane1Tests.Skipped", result.FirstUndeclared);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, false)]
    [Xunit.InlineData(2, false)]
    [Xunit.InlineData(1, true)]
    public void IncompleteEvidence_FallsBackEvenWhenKnownIdentityIsDeclared(int? count, bool unreadable)
    {
        var result = RemoteLaneNotExecutedDecision.Decide(count,
            new(["Lane0Tests.Skipped"], unreadable),
            new HashSet<string>(StringComparer.Ordinal) { "Lane0Tests.Skipped" });

        Assert.False(result.Accept);
        Assert.Equal("unidentified", result.FirstUndeclared);
    }
}
