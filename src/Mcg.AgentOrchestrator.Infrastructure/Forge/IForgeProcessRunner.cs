namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IForgeProcessRunner
{
    ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout);
}
