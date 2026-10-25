using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ActivationAdmissionPermitHolders
{
    private static readonly AsyncLocal<Func<IReadOnlyList<string>>?> HolderLabelProbe = new();

    internal static int CountEvidenceHeld() => CountEvidenceHeld(DotnetBuildEnvironmentManager.CaptureStorageRoot());

    internal static int CountEvidenceHeld(DotnetBuildStorageRoot storageRoot) =>
        (HolderLabelProbe.Value?.Invoke() ?? DotnetBuildEnvironmentManager.ReadHeldBuildPermitHolderLabels(storageRoot))
        .Count(label => label.StartsWith("pre-review-evidence:", StringComparison.Ordinal) ||
            label.StartsWith("worker-build:", StringComparison.Ordinal));

    internal static IDisposable PushHolderLabelProbe(Func<IReadOnlyList<string>> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var previous = HolderLabelProbe.Value;
        HolderLabelProbe.Value = probe;
        return new RestoreAction(() => HolderLabelProbe.Value = previous);
    }

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
