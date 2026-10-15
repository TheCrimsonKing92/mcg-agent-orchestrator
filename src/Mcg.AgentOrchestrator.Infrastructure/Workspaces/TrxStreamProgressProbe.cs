using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class TrxStreamProgressProbe(string resultsDirectory) : IAcceptanceLaneProgressProbe
{
    public string? Sample()
    {
        var count = 0;
        long totalLength = 0;
        long latestWriteTicks = 0;
        try
        {
            if (!Directory.Exists(resultsDirectory))
                return null;

            var files = new DirectoryInfo(resultsDirectory).EnumerateFiles("trx-stream-*.bin",
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchType = MatchType.Simple
                });
            foreach (var file in files)
            {
                try
                {
                    file.Refresh();
                    var length = file.Length;
                    var writeTicks = file.LastWriteTimeUtc.Ticks;
                    count++;
                    totalLength += length;
                    latestWriteTicks = Math.Max(latestWriteTicks, writeTicks);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A disappearing or inaccessible file is absent from this sample.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retain any files collected before enumeration was interrupted.
        }
        catch
        {
            // Optional progress observation must never fail the acceptance lane.
        }

        return count == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{count}:{totalLength}:{latestWriteTicks}");
    }
}
