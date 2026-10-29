using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The application owns its process data root; lower layers receive only this resolver.
internal static class CrosswalkExecutableComposition
{
    private static readonly Lazy<CrosswalkExecutableResolver> Executable = new(() =>
        new(dataRoot: OrchestratorDataRoot.Resolve().RootDirectory));

    internal static CrosswalkExecutableResolver ForProcess => Executable.Value;

    internal static void Install() => CrosswalkShadowDetect.Install(new(new CrosswalkClient(ForProcess)));
}
