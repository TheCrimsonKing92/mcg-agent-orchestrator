namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OperatorChannelFactory
{
    public static DeadManHeartbeatOptions BuildDeadManHeartbeatOptions(OperatorChannelCatalog catalog)
    {
        if (!catalog.DeadManHeartbeatEnabled ||
            string.IsNullOrWhiteSpace(catalog.DeadManHeartbeatUrl) ||
            !Uri.TryCreate(catalog.DeadManHeartbeatUrl, UriKind.Absolute, out var endpoint))
        {
            return new DeadManHeartbeatOptions();
        }

        return new DeadManHeartbeatOptions(Enabled: true, Endpoint: endpoint);
    }
}
