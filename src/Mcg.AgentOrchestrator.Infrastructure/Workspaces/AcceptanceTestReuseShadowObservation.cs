namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceTestReuseShadowObservation(
    string Class, string Check, bool Failed, bool ExclusiveResourceLane);
