using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The application owns its process data root; lower layers receive only this resolver.
internal static class CrosswalkExecutableComposition
{
    private static readonly ConcurrentDictionary<string, CrosswalkExecutableResolver> Executables =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static CrosswalkExecutableResolver ForProcess => Executables.GetOrAdd(
        OrchestratorDataRoot.Resolve().RootDirectory, static root => new(dataRoot: root));

    internal static CrosswalkStackReader ForOnboarding() =>
        new(ForProcess, new AcceptanceGateEngineSettings().ResolveDiscoveryTimeout());

    // Attempt hosts need the installation root after their profile and environment are scrubbed.
    // Their verification descendants still receive the normal hermetic environment.
    internal static void PropagateDataRoot(IDictionary<string, string?> environment) =>
        environment[OrchestratorDataRoot.EnvironmentVariable] = OrchestratorDataRoot.Resolve().RootDirectory;

    internal static void Install() => CrosswalkShadowDetect.Install(new(new CrosswalkClient(ForProcess)));
}
