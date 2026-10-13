using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Resolve and snapshot the actual bytes, both at application and when projecting current proof.
public static class EpicOutcomeReceipts
{
    public static byte[] Read(EpicOutcomeSource source)
    {
        if (!Path.IsPathFullyQualified(source.Locator)) throw new ArgumentException("outcome-evidence-path-not-absolute");
        try
        {
            using var stream = new FileStream(source.Locator, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > EpicOutcomeContract.MaximumReceiptBytes)
                throw new InvalidOperationException("outcome-receipt-size-invalid");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("outcome-receipt-identity-mismatch");
            return bytes;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidOperationException("outcome-missing-receipt", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("outcome-inaccessible-evidence", error); }
    }

    public static string? Problem(EpicOutcomeObservation observation)
    {
        try
        {
            var snapshot = Convert.FromBase64String(observation.SnapshotBase64);
            if (snapshot.Length is <= 0 or > EpicOutcomeContract.MaximumReceiptBytes ||
                !Convert.ToHexString(SHA256.HashData(snapshot)).Equals(observation.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                return "receipt-identity-mismatch";
            Read(observation.Source);
            return null;
        }
        catch (FormatException) { return "missing-receipt"; }
        catch (ArgumentException) { return "ambiguous-provenance"; }
        catch (InvalidOperationException error) { return error.Message.Replace("outcome-", "", StringComparison.Ordinal); }
    }
}
