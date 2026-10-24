namespace Mcg.AgentOrchestrator.Infrastructure;

// Diagnostic custody only: retention failures must not prevent local fallback.
internal static class RemoteLaneRedEvidence
{
    internal static string Retain(IReadOnlyList<string> fetchedTrxPaths, string? attemptPrefix,
        string executorId, string lane, int exitCode)
    {
        var missing = $"exit={exitCode} trx=missing";
        try
        {
            if (string.IsNullOrWhiteSpace(attemptPrefix)) return missing;
            var folder = Path.GetDirectoryName(attemptPrefix);
            if (string.IsNullOrWhiteSpace(folder)) return missing;
            Directory.CreateDirectory(folder);
            var laneKey = SshRemoteLaneExecutor.LaneKey(lane);
            var copied = new List<string>();
            foreach (var source in fetchedTrxPaths)
            {
                string? temporary = null;
                try
                {
                    if (!File.Exists(source)) continue;
                    var destination = Path.Combine(folder,
                        AcceptanceArtifactFileNames.BoundFileName($"remote-red-{executorId}-{laneKey}", $"-{copied.Count + 1}.trx"));
                    temporary = destination + $".tmp-{Guid.NewGuid():N}";
                    File.Copy(source, temporary);
                    File.Move(temporary, destination, overwrite: true);
                    copied.Add(destination);
                }
                catch (Exception) { /* Skip this receipt and retain any later evidence. */ }
                finally
                {
                    if (temporary is not null)
                        try { File.Delete(temporary); }
                        catch (Exception) { /* Cleanup cannot change fallback. */ }
                }
            }

            foreach (var path in copied)
            {
                try
                {
                    var trx = AcceptanceTrxFailureReader.Read(path);
                    if (trx.Status != AcceptanceTrxReadStatus.Readable || trx.Failures.Count == 0) continue;
                    var first = trx.Failures[0];
                    var name = string.IsNullOrWhiteSpace(first.TestName) ? "unknown" : first.TestName;
                    var message = string.IsNullOrWhiteSpace(first.Message) ? "" : first.Message;
                    if (message.Length > 300) message = message[..300];
                    return $"exit={exitCode} failed={trx.Failures.Count} first={name}: {message}";
                }
                catch (Exception) { /* An unreadable receipt does not hide later failures. */ }
            }
        }
        catch (Exception) { /* Optional diagnostics must never escape into execution. */ }
        return missing;
    }
}
