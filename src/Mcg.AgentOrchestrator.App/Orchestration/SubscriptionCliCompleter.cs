using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SubscriptionCliCompletionResult(
    string StandardOutput,
    int ExitCode,
    string? FailureReason = null)
{
    public bool Succeeded => ExitCode == 0;
}

// Runs a single subscription-CLI text completion using a worker profile without dispatching a
// tracked task. Used only for orchestrator-internal advisory functions.
internal sealed class SubscriptionCliCompleter
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(180);

    private readonly string _commandTemplate;
    private readonly string _profileName;
    private readonly string _modelAlias;
    private readonly string? _reasoningEffort;
    private readonly Func<string, string, string?, CancellationToken, Task<SubscriptionCliCompletionResult>> _runner;

    public SubscriptionCliCompleter(
        WorkerProfileCatalog profiles,
        string profileName,
        string modelAlias,
        string? reasoningEffort = null)
        : this(
            profiles.GetRequired(profileName).CommandTemplate,
            profileName,
            modelAlias,
            reasoningEffort,
            (command, workingDirectory, standardInput, cancellationToken) =>
                RunCommandWithResultAsync(command, workingDirectory, standardInput, cancellationToken))
    {
    }

    internal SubscriptionCliCompleter(
        string commandTemplate,
        string profileName,
        string modelAlias,
        string? reasoningEffort,
        Func<string, string, CancellationToken, Task<string>> runner)
        : this(commandTemplate, profileName, modelAlias, reasoningEffort, (command, workingDirectory, _, cancellationToken) => runner(command, workingDirectory, cancellationToken))
    {
    }

    internal SubscriptionCliCompleter(
        string commandTemplate,
        string profileName,
        string modelAlias,
        string? reasoningEffort,
        Func<string, string, string?, CancellationToken, Task<string>> runner)
        : this(
            commandTemplate,
            profileName,
            modelAlias,
            reasoningEffort,
            async (command, workingDirectory, standardInput, cancellationToken) =>
                new SubscriptionCliCompletionResult(
                    await runner(command, workingDirectory, standardInput, cancellationToken).ConfigureAwait(false),
                    0))
    {
    }

    internal SubscriptionCliCompleter(
        string commandTemplate,
        string profileName,
        string modelAlias,
        string? reasoningEffort,
        Func<string, string, string?, CancellationToken, Task<SubscriptionCliCompletionResult>> runner)
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
        CancellationToken cancellationToken) =>
        (await CompleteWithResultAsync(prompt, promptFileName, cancellationToken).ConfigureAwait(false)).StandardOutput;

    internal async Task<SubscriptionCliCompletionResult> CompleteWithResultAsync(
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
            var standardInput = _commandTemplate.Contains("{promptPath}", StringComparison.OrdinalIgnoreCase)
                ? null
                : prompt;
            return await _runner(command, tempDir, standardInput, cancellationToken).ConfigureAwait(false);
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
        return await RunCommandAsync(command, workingDirectory, null, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> RunCommandAsync(
        string command,
        string workingDirectory,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var result = await RunCommandWithResultAsync(
            command,
            workingDirectory,
            standardInput,
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    internal static async Task<SubscriptionCliCompletionResult> RunCommandWithResultAsync(
        string command,
        string workingDirectory,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var result = await WorkerProcessRunner.RunBufferedAsync(
            new WorkerProcessRunRequest(command, workingDirectory, StandardInput: standardInput),
            cancellationToken).ConfigureAwait(false);
        return new SubscriptionCliCompletionResult(
            result.StandardOutput.Trim(),
            result.ExitCode,
            result.ExitCode == 0 ? null : $"subscription-cli-exit:{result.ExitCode}");
    }
}
