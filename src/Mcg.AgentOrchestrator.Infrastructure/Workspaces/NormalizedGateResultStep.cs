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
                return WithFailure(receipt, new(target, NormalizedTestResultWriteFailure.SourceNotReadable,
                    read.Detail ?? read.Status.ToString()));
            NormalizedTestResultWriter.Write(target, sourceLabel, read.Records);
            return receipt with { NormalizedTestResultPaths = [.. (receipt.NormalizedTestResultPaths ?? []), target] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var firstLine = ex.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? "No message provided.";
            return WithFailure(receipt, new(target, NormalizedTestResultWriteFailure.WriteFailed,
                $"{ex.GetType().Name}: {firstLine}"));
        }
    }

    private static AcceptanceCheckResult WithFailure(AcceptanceCheckResult receipt, NormalizedTestResultWriteFailure failure) =>
        receipt with
        {
            // The contract has one typed failure: keep its identity and append later diagnostics.
            NormalizedTestResultWriteFailure = receipt.NormalizedTestResultWriteFailure is { } previous
                ? previous with { Detail = $"{previous.Detail}\nTargetPath={failure.TargetPath}; Reason={failure.Reason}; Detail={failure.Detail}" }
                : failure
        };
}
