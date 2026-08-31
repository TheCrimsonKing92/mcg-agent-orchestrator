using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class HermesAcpCliCommand
{
    public const string Usage =
        "Usage: hermes-acp-trial --confirm-live-hermes-start [--prompt <path>] [--prompt-sha256 <sha256>] " +
        "[--workspace <path>] [--sandbox <path>] --provider <provider> --model <model> [--receipt <path>]. " +
        "Trial-compare may supply prompt path/hash through MCG_TRIAL_BRIEF_PATH/MCG_TRIAL_BRIEF_SHA256.";

    internal static async Task<HermesAcpTerminalReceipt> ExecuteAsync(
        IReadOnlyList<string> parts,
        TextWriter output,
        TextWriter error,
        Func<HermesAcpRequest, string, TextWriter, CancellationToken, Task<HermesAcpTerminalReceipt>>? run = null,
        Func<string?>? containedTrialState = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (!parts.Any(part => part.Equals("--confirm-live-hermes-start", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "hermes-acp-trial requires --confirm-live-hermes-start; this command starts the pinned external Hermes process.");
        }

        var promptPath = ValueAfter(parts, "--prompt") ?? Environment.GetEnvironmentVariable("MCG_TRIAL_BRIEF_PATH");
        var promptSha256 = ValueAfter(parts, "--prompt-sha256") ?? Environment.GetEnvironmentVariable("MCG_TRIAL_BRIEF_SHA256");
        var workspace = Path.GetFullPath(ValueAfter(parts, "--workspace") ?? Environment.CurrentDirectory);
        var sandboxValue = ValueAfter(parts, "--sandbox") ??
            (string.IsNullOrWhiteSpace(promptPath) ? null : Path.GetDirectoryName(Path.GetFullPath(promptPath)));
        var provider = ValueAfter(parts, "--provider");
        var model = ValueAfter(parts, "--model");

        if (string.IsNullOrWhiteSpace(promptPath) || string.IsNullOrWhiteSpace(promptSha256) ||
            string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(sandboxValue))
        {
            throw new ArgumentException(Usage);
        }

        promptPath = Path.GetFullPath(promptPath);
        var sandbox = Path.GetFullPath(sandboxValue);
        RequireContainedTrialRoot(workspace, sandbox, containedTrialState);
        Directory.CreateDirectory(sandbox);
        var receiptPath = Path.GetFullPath(
            ValueAfter(parts, "--receipt") ?? Path.Combine(sandbox, HermesAcpAdapter.TerminalReceiptFileName));
        var request = new HermesAcpRequest(
            promptPath,
            promptSha256,
            workspace,
            sandbox,
            model,
            provider);
        run ??= (candidate, path, progress, token) =>
            new HermesAcpLifecycle().RunAsync(candidate, path, progress, token);

        var receipt = await run(request, receiptPath, error, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(receipt.FinalOutput).ConfigureAwait(false);
        if (!receipt.FinalOutput.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            await output.WriteLineAsync().ConfigureAwait(false);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await error.WriteLineAsync($"[hermes-acp] terminal receipt: {receiptPath}").ConfigureAwait(false);
        await error.FlushAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    private static void RequireContainedTrialRoot(
        string workspace,
        string sandbox,
        Func<string?>? containedTrialState)
    {
        containedTrialState ??= () => Environment.GetEnvironmentVariable("MCG_TRIAL_HARNESS_STATE");
        var harnessState = containedTrialState();
        if (string.IsNullOrWhiteSpace(harnessState))
        {
            throw new InvalidOperationException(
                "hermes-acp-trial must run inside the contained trial root created by trial-compare.");
        }

        var stateDirectory = Directory.GetParent(Path.GetFullPath(harnessState));
        var trialRoot = stateDirectory?.Name.Equals(".trial-state", StringComparison.OrdinalIgnoreCase) == true
            ? stateDirectory.Parent?.FullName
            : null;
        if (trialRoot is null || !IsWithin(workspace, trialRoot) || !IsWithin(sandbox, trialRoot))
        {
            throw new InvalidOperationException(
                "hermes-acp-trial workspace and sandbox must remain inside the contained trial root.");
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static string? ValueAfter(IReadOnlyList<string> parts, string flag)
    {
        for (var index = 1; index < parts.Count; index++)
        {
            if (!parts[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= parts.Count || parts[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{flag} requires a value. {Usage}");
            }

            return parts[index + 1];
        }

        return null;
    }
}
