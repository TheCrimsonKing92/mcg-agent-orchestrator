using System.Globalization;

internal enum PidFileReadOutcome { Missing, Unparsable, IoException, Ready }

internal static class PidFilePolling
{
    internal static async Task<int> WaitForPidAsync(
        string pidFilePath,
        CancellationToken cancellationToken,
        Action<PidFileReadOutcome>? observer = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = PidFileReadOutcome.Missing;
            var pid = 0;
            if (File.Exists(pidFilePath))
            {
                try
                {
                    outcome = int.TryParse(File.ReadAllText(pidFilePath).Trim(), CultureInfo.InvariantCulture, out pid)
                        && pid > 0
                        ? PidFileReadOutcome.Ready
                        : PidFileReadOutcome.Unparsable;
                }
                catch (IOException)
                {
                    // A sharing violation means the published pid is not yet readable.
                    outcome = PidFileReadOutcome.IoException;
                }
            }

            observer?.Invoke(outcome);
            if (outcome == PidFileReadOutcome.Ready)
                return pid;

            await Task.Delay(25, cancellationToken);
        }
    }
}
