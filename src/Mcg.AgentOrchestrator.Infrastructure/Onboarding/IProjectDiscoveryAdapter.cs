using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IProjectDiscoveryAdapter
{
    ProjectModel Discover(string repositoryRoot, IReadOnlyCollection<string>? excludedDirectoryNames = null,
        IUnitCommandMeasurer? measurer = null, UnitCommandKinds measuredKinds = UnitCommandKinds.All);
}
