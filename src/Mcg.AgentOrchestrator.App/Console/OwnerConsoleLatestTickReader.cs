using Mcg.AgentOrchestrator.App.Cli;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleLatestTickReader(OwnerConsoleJsonlTail conductTail, string runEventStorePath)
{
    internal DateTimeOffset? Read()
    {
        DateTimeOffset? tick = null;
        try
        {
            foreach (var item in conductTail.Select("latest-tick", ConductorStatusSnapshotReader.ParseEventLine, long.MaxValue))
                if (item is not null && item.Kind.StartsWith("tick", StringComparison.OrdinalIgnoreCase) &&
                    (tick is null || item.At > tick)) tick = item.At;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return ConductorStatusSnapshotReader.LatestTick(tick, runEventStorePath);
    }
}
