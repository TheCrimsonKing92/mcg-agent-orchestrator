namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class NormalizedGateResultStep
{
    internal const string TrxSourceLabel = "trx";

    internal static AcceptanceCheckResult Apply(AcceptanceCheckResult receipt, string trxPath, string sourceLabel)
    {
        var target = NormalizedTestResultWriter.PathFor(trxPath);
        try
        {
            var read = new TrxTestResultSource().Read(trxPath);
            if (read.Status != TestResultReadStatus.Readable)
                return receipt with
                {
                    NormalizedTestResultWriteFailure = new(target, NormalizedTestResultWriteFailure.SourceNotReadable,
                        read.Detail ?? read.Status.ToString())
                };
            NormalizedTestResultWriter.Write(target, sourceLabel, read.Records);
            return receipt with { NormalizedTestResultPaths = [target] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var firstLine = ex.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? "No message provided.";
            return receipt with
            {
                NormalizedTestResultWriteFailure = new(target, NormalizedTestResultWriteFailure.WriteFailed,
                    $"{ex.GetType().Name}: {firstLine}")
            };
        }
    }
}
