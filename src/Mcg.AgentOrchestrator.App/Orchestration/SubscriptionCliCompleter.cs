using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Runs a single subscription-CLI text completion using a worker profile without dispatching a
// tracked task. Used only for orchestrator-internal advisory functions.
internal sealed class SubscriptionCliCompleter
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    private readonly string _commandTemplate;
    private readonly string _profileName;
    private readonly string _modelAlias;
    private readonly string? _reasoningEffort;
    private readonly Func<string, string, CancellationToken, Task<string>> _runner;

    public SubscriptionCliCompleter(
        WorkerProfileCatalog profiles,
        string profileName,
        string modelAlias,
        string? reasoningEffort = null)
        : this(profiles.GetRequired(profileName).CommandTemplate, profileName, modelAlias, reasoningEffort, RunCommandAsync)
    {
    }

    internal SubscriptionCliCompleter(
        string commandTemplate,
        string profileName,
        string modelAlias,
        string? reasoningEffort,
        Func<string, string, CancellationToken, Task<string>> runner)
    {
        _commandTemplate = commandTemplate;
        _profileName = profileName;
        _modelAlias = modelAlias;
        _reasoningEffort = reasoningEffort;
        _runner = runner;
    }

    public string Name => $"sub:{_profileName}:{_modelAlias}";

    public async Task<string> CompleteAsync(
        string prompt,
        string promptFileName,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcg-cli-complete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var promptPath = Path.Combine(tempDir, promptFileName);
        try
        {
            await File.WriteAllTextAsync(promptPath, prompt, cancellationToken).ConfigureAwait(false);
            var command = SubstitutePlaceholders(_commandTemplate, _profileName, promptPath, _modelAlias, _reasoningEffort, tempDir);
            return await _runner(command, tempDir, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    internal static string SubstitutePlaceholders(
        string template,
        string profileName,
        string promptPath,
        string modelAlias,
        string? reasoningEffort,
        string workingDirectory)
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile(profileName);
        var permissionMode = provider.Identity.Kind == ProviderKind.AnthropicClaudeCli ? "default" : "plan";
        return template
            .Replace("{promptPath}", Quote(promptPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{subscriptionModelName}", Quote(modelAlias), StringComparison.OrdinalIgnoreCase)
            .Replace("{subscriptionReasoningEffort}", Quote(string.IsNullOrWhiteSpace(reasoningEffort) ? AgentCatalog.ComplexReasoningEffort : reasoningEffort), StringComparison.OrdinalIgnoreCase)
            .Replace("{sandboxMode}", Quote("read-only"), StringComparison.OrdinalIgnoreCase)
            .Replace("{permissionMode}", Quote(permissionMode), StringComparison.OrdinalIgnoreCase)
            .Replace("{workingDirectory}", Quote(workingDirectory), StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    // Extracted so tests can assert ProcessStartInfo properties without launching a real process.
    internal static System.Diagnostics.ProcessStartInfo BuildStartInfo(string command, string workingDirectory) =>
        WorkerProcessRunner.BuildPowerShellStartInfo(command, workingDirectory);

    internal static async Task<string> RunCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, workingDirectory),
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }
}
