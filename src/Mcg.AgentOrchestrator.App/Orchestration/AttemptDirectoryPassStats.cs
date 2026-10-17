namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AttemptDirectoryPassStats(
    string Directory, int Candidates, int Deleted, int Enumerations, int FullPathHashes);
