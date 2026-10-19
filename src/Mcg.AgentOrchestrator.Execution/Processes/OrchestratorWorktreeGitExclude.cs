namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns local exclude entries for files the orchestrator writes during dispatch.
internal static class OrchestratorWorktreeGitExclude
{
    internal const string HandoffFileName = ".orchestrator-handoff.md";
    private const string SandboxEntry = ".mcg-sandbox/";

    internal static IReadOnlyList<string> DispatchArtifactEntries { get; } = Array.AsReadOnly<string>(
    [
        "/" + WorkerSandboxPreparer.MarkerFileName,
        "/" + WorkerSandboxPreparer.ReceiptFileName,
        "/" + HandoffFileName
    ]);

    internal static bool Apply(string worktree) =>
        LocalGitExclude.TryAppendEntries(worktree, [SandboxEntry, ..DispatchArtifactEntries], IsEntryPresent);

    private static bool IsEntryPresent(string existing, string entry) =>
        entry == SandboxEntry && existing.Split('\n').Any(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Contains(".mcg-sandbox", StringComparison.Ordinal)
                && trimmed != WorkerSandboxPreparer.ReceiptFileName
                && trimmed != "/" + WorkerSandboxPreparer.ReceiptFileName;
        });
}
