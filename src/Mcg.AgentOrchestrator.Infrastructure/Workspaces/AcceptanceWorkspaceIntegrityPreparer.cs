namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceWorkspaceIntegrityPreparer
{
    private static readonly AsyncLocal<IWorkerIntegrityLabeler?> IntegrityLabelerOverrideForTests = new();

    internal static IDisposable PushIntegrityLabelerForTests(IWorkerIntegrityLabeler integrityLabeler)
    {
        ArgumentNullException.ThrowIfNull(integrityLabeler);
        var previous = IntegrityLabelerOverrideForTests.Value;
        IntegrityLabelerOverrideForTests.Value = integrityLabeler;
        return new RestoreAction(() => IntegrityLabelerOverrideForTests.Value = previous);
    }

    internal static void Prepare(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var integrityLabeler = IntegrityLabelerOverrideForTests.Value ?? new IcaclsIntegrityLabeler();
        if (!integrityLabeler.SetIntegrity(
                workspacePath,
                WorkerSandboxPreparer.LowInheritableLevel,
                recursive: true))
        {
            throw new InvalidOperationException(
                $"Failed to prepare acceptance workspace '{workspacePath}' for Low-integrity gate writes.");
        }

        DispatchProcessHost.ProtectWorkspaceBoundary(workspacePath, integrityLabeler);
        DispatchProcessHost.ProtectGitMetadata(workspacePath, integrityLabeler);
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
