using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogItemIdPrefixResolver
{
    internal static BacklogItem? Resolve(string backlogStorePath, string idPrefix, bool explicitFlag)
    {
        var trimmed = idPrefix.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            if (explicitFlag)
            {
                throw new ArgumentException("--backlog-item requires an id prefix.");
            }

            return null;
        }

        try
        {
            var item = new BacklogStore(backlogStorePath).GetByIdPrefixAsync(trimmed).GetAwaiter().GetResult();
            if (item is null && explicitFlag)
            {
                throw new InvalidOperationException($"No backlog item found with id prefix '{trimmed}'.");
            }

            return item;
        }
        catch (InvalidOperationException) when (!explicitFlag)
        {
            throw;
        }
    }
}
