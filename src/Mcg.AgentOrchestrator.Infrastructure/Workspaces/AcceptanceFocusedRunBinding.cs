namespace Mcg.AgentOrchestrator.Infrastructure;

// Execution-local binding: the build and test phases must use this same environment.
public sealed record AcceptanceFocusedRunBinding(DotnetBuildEnvironment Environment, string MemberId);
