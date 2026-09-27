using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class ConductorDriverTestsIncompleteScopeRetryContextBounds
{
    [Fact]
    public void AccountExtractionAcceptsCrLfAndKeepsLastTwentyFiveProseLines()
    {
        var lines = Enumerable.Range(0, 40).Select(index => $"account line {index:D2}").ToArray();
        var lf = string.Join("\n", lines) + "\n" + ConductorDriverTestsIncompleteScopeRetryContext.ResultBlock;
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        var account = ConductorDriver.BuildPreviousRoundAccount(lf);
        Assert.Equal(account, ConductorDriver.BuildPreviousRoundAccount(crlf));
        Assert.DoesNotContain("account line 14", account, StringComparison.Ordinal);
        Assert.Contains("account line 15", account, StringComparison.Ordinal);
        Assert.Contains("account line 39", account, StringComparison.Ordinal);
        Assert.Equal(25, account.Split("account line ", StringSplitOptions.None).Length - 1);
        Assert.Contains("files: src/Feature.cs", account, StringComparison.Ordinal);
        Assert.Contains("assigned_scope_complete: false", account, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountProseHasHardCharacterCapAndMissingResultHasUnavailableEntry()
    {
        var longProse = new string('x', 1500) + new string('y', 2500);
        var account = ConductorDriver.BuildPreviousRoundAccount(
            longProse + "\n" + ConductorDriverTestsIncompleteScopeRetryContext.ResultBlock);
        var prose = account[(account.LastIndexOf("assigned_scope_complete: false", StringComparison.Ordinal) +
            "assigned_scope_complete: false".Length + Environment.NewLine.Length)..];
        Assert.Equal(2500, prose.Length);
        Assert.Equal(new string('y', 2500), prose);
        Assert.Equal("Previous round's account:" + Environment.NewLine + "previous round account unavailable",
            ConductorDriver.BuildPreviousRoundAccount("closing prose without a result"));
    }

    [Fact]
    public void LastWorkerResultUsesOnlyItsImmediatelyPrecedingProse()
    {
        var output = "earlier prose\nWORKER_RESULT:\nfiles: old.cs\nEND_WORKER_RESULT\n" +
            "final account\nWORKER_RESULT:\nfiles: current.cs\nEND_WORKER_RESULT";

        var account = ConductorDriver.BuildPreviousRoundAccount(output);
        Assert.Contains("files: current.cs", account, StringComparison.Ordinal);
        Assert.Contains("final account", account, StringComparison.Ordinal);
        Assert.DoesNotContain("old.cs", account, StringComparison.Ordinal);
        Assert.DoesNotContain("earlier prose", account, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryAccountShapeKeepsRetryCauseTargetAndBudget(int shape)
    {
        var prose = string.Join("\n", Enumerable.Range(0, 40).Select(index => $"account line {index:D2}"));
        var output = shape switch
        {
            0 => ConductorDriverTestsIncompleteScopeRetryContext.ClosingAccount + "\n" +
                 ConductorDriverTestsIncompleteScopeRetryContext.ResultBlock,
            1 => (ConductorDriverTestsIncompleteScopeRetryContext.ClosingAccount + "\n" +
                  ConductorDriverTestsIncompleteScopeRetryContext.ResultBlock)
                .Replace("\n", "\r\n", StringComparison.Ordinal),
            2 => prose + "\n" + ConductorDriverTestsIncompleteScopeRetryContext.ResultBlock,
            _ => "closing prose without a WORKER_RESULT"
        };
        var result = ConductorDriverTestsIncompleteScopeRetryContext.RunRecovery(output);

        Assert.Equal(result.Developer.Id, result.RetriedTask);
        Assert.Equal(RetryCause.ContractClarification, result.RetryCause);
        Assert.Equal(
            $"Auto-retry real worker/command failure for task {result.Developer.Id.Value[..8]} " +
            "(attempt 1/2); Failed command: test.exe; " +
            "Failure evidence: Developer declared the assigned implementation scope incomplete.",
            result.RetryMessage);
        Assert.Equal(1, result.Developer.CriterionRetryCount);
        Assert.Equal(1, result.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(1, result.Starts);
        Assert.Equal(3, result.Feedback.Count);
        if (shape == 3)
            Assert.Equal("Previous round's account:" + Environment.NewLine + "previous round account unavailable",
                result.Feedback[2]);
    }
}
