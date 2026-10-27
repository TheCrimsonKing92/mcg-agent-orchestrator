namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class GoTestJsonTestResultSource : ITestResultSource
{
    public string ReportedBy => "go-test-json";

    public TestResultReadResult Read(string reportPath)
    {
        string output;
        try
        {
            output = File.ReadAllText(reportPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(reportPath, TestResultReadStatus.Missing, Array.Empty<TestResult>(),
                AcceptanceTrxOutcomeTaxonomy.FirstNonEmptyLine(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(reportPath, TestResultReadStatus.Unreadable, Array.Empty<TestResult>(),
                AcceptanceTrxOutcomeTaxonomy.FirstNonEmptyLine(ex.Message));
        }

        var records = GoTestJsonGateReceipt.ReadTopLevelRows(output, null).Select(row =>
            new TestResult($"{row.Package}.{row.Test}", $"{row.Package}.{row.Test}", null, new[] { row.Package },
                row.Failed ? TestResultStatus.Failed : TestResultStatus.Passed, null, null, null,
                Array.Empty<TestResultAttempt>(), null, null, ReportedBy)).ToArray();
        return new(reportPath, TestResultReadStatus.Readable, records, null);
    }
}
