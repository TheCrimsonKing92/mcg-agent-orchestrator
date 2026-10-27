namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record NormalizedTestResultWriteFailure(string TargetPath, string Reason, string Detail)
{
    public const string SourceNotReadable = "source-not-readable";
    public const string WriteFailed = "write-failed";
}
