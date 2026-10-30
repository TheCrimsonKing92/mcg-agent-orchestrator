using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectDeliveryShow
{
    internal static void Write(OrchestratorProject project, string defaultRootDirectory, string dataRootDirectory, TextWriter output)
    {
        output.WriteLine($"Delivery: {project.Delivery} (source: {(project.DeliveryAnswer is null ? "default" : "owner answer")})");
        output.WriteLine($"Delivery decider: {project.DeliveryDecider ?? "unset (repository owner)"}");
        var probe = ReadProbe(project, defaultRootDirectory, dataRootDirectory);
        output.WriteLine(probe is null ? "Delivery probe: not recorded" : $"Delivery probe: {probe.Result}: {probe.Reason}");
    }

    private static DeliveryProbeRecord? ReadProbe(OrchestratorProject project, string defaultRootDirectory, string dataRootDirectory)
    {
        try
        {
            var directory = ProjectDiscoveryModelLocation.ResolveDirectory(project, null, defaultRootDirectory, dataRootDirectory);
            return ProjectModelJson.Deserialize(File.ReadAllText(Path.Combine(directory, "project-model.json"))).DeliveryProbe;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
