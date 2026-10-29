namespace Mcg.AgentOrchestrator.Infrastructure;

// A resolution-time snapshot of the selected executable, shared by invocation evidence.
public sealed record CrosswalkExecutableIdentity(string Path, string Version, string Sha256);
