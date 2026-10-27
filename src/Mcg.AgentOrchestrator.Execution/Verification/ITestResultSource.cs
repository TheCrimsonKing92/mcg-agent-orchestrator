namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface ITestResultSource
{
    string ReportedBy { get; }
    TestResultReadResult Read(string reportPath);
}
