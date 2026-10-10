using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneNotExecutedIdentityReaderTests
{
    [Xunit.Fact]
    public void MixedOutcomes_ReturnsExactlyNotExecutedIdentities()
    {
        var trx = XDocument.Parse("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <TestDefinitions>
                <UnitTest id="passed"><TestMethod className="Lane0Tests" name="Executes" /></UnitTest>
                <UnitTest id="first"><TestMethod className="Lane0Tests" name="Skipped" /></UnitTest>
                <UnitTest id="second"><TestMethod className="Lane1Tests" name="Skipped" /></UnitTest>
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="passed" testName="display passes" outcome="Passed" />
                <UnitTestResult testId="first" testName="display skips" outcome="NotExecuted" />
                <UnitTestResult testId="second" testName="display skips(1)" outcome="NotExecuted" />
              </Results>
            </TestRun>
            """);

        Assert.Equal(new[] { "Lane0Tests.Skipped", "Lane1Tests.Skipped(1)" },
            RemoteLaneNotExecutedIdentityReader.Read(trx));
    }

    [Xunit.Fact]
    public void MissingReceipt_IsUnreadable()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.trx");

        var result = RemoteLaneNotExecutedIdentityReader.Read([path]);

        Assert.True(result.Unreadable);
        Assert.Empty(result.Identities);
    }
}
