namespace Mcg.AgentOrchestrator.Core;

public sealed record UnitTestSetup(
    string UnitId,
    ProjectFact<string> Framework,
    ProjectFact<string> Runner);
