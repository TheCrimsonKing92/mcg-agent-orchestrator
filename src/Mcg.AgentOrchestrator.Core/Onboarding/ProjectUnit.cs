namespace Mcg.AgentOrchestrator.Core;

public sealed record ProjectUnit(
    string Id,
    ProjectFact<string> Name,
    ProjectFact<string> Location,
    ProjectFact<bool?> IsTest);
