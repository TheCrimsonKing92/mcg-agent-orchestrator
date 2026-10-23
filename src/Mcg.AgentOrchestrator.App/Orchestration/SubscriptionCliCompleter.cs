using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SubscriptionCliCompletionResult(
    string StandardOutput,
    int ExitCode,
    string? FailureReason = null)
{
    public bool Succeeded => ExitCode == 0;
    public string RawStandardOutput { get; init; } = StandardOutput;
}

internal sealed record SubscriptionCliSemanticTextResult(
    string Text,
    string RawStandardOutput,
    int ExitCode,
    string? FailureReason)
{
    public bool Succeeded => ExitCode == 0 && FailureReason is null;
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
        CancellationToken cancellationToken) =>
        (await InvokeAsync(prompt, promptFileName, cancellationToken).ConfigureAwait(false)).Result;

    internal async Task<SubscriptionCliSemanticTextResult> CompleteSemanticTextWithResultAsync(
        string prompt,
        string promptFileName,
        CancellationToken cancellationToken)
    {
        var (result, command) = await InvokeAsync(prompt, promptFileName, cancellationToken).ConfigureAwait(false);
        // Do not pass provider text into a failure diagnostic: it can contain the prompt or stream.
        if (!result.Succeeded)
            return new(string.Empty, result.RawStandardOutput, result.ExitCode, $"subscription-cli-exit:{result.ExitCode}");

        var kind = WorkerProviderCatalog.Default().ResolveProfile(_profileName).Identity.Kind;
        if (kind is not (ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexLuna) || !HasJsonArgument(command))
            return new(result.StandardOutput, result.RawStandardOutput, result.ExitCode, null);

        return CodexJsonlUsageParser.TryParseCompletedAnswer(result.RawStandardOutput, out var answer, out var failure)
            ? new(answer, result.RawStandardOutput, result.ExitCode, null)
            : new(string.Empty, result.RawStandardOutput, result.ExitCode, failure);
    }

    private async Task<(SubscriptionCliCompletionResult Result, string Command)> InvokeAsync(
        string prompt,
        string promptFileName,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("cli-complete"), $"mcg-cli-complete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var promptPath = Path.Combine(tempDir, promptFileName);
        try
        {
            await File.WriteAllTextAsync(promptPath, prompt, cancellationToken).ConfigureAwait(false);
            var command = SubstitutePlaceholders(_commandTemplate, _profileName, promptPath, _modelAlias, _reasoningEffort, tempDir);
            var standardInput = _commandTemplate.Contains("{promptPath}", StringComparison.OrdinalIgnoreCase)
                ? null
                : prompt;
            return (await _runner(command, tempDir, standardInput, cancellationToken).ConfigureAwait(false), command);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // Read literal PowerShell arguments, respecting quoted segments and escaped characters. In
    // particular, a flag inside a quoted prompt, path, or nested command is not its own argument.
    private static bool HasJsonArgument(string command)
    {
        var token = new StringBuilder();
        char quote = '\0';
        var found = false;
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '`' && quote != '\'' && i + 1 < command.Length)
            {
                token.Append(command[++i]);
            }
            else if (quote != '\0')
            {
                if (ch == quote)
                {
                    if (quote == '\'' && i + 1 < command.Length && command[i + 1] == '\'')
                        token.Append(command[++i]);
                    else
                        quote = '\0';
                }
                else
                    token.Append(ch);
            }
            else if (ch is '\'' or '"')
                quote = ch;
            else if (ch == '<' && token.Length == 0 && i + 1 < command.Length && command[i + 1] == '#')
            {
                // PowerShell block comments end at the first #>; their contents are not arguments.
                i += 2;
                while (i + 1 < command.Length && !(command[i] == '#' && command[i + 1] == '>')) i++;
                if (i + 1 >= command.Length)
                    return false;
                i++;
            }
            else if (char.IsWhiteSpace(ch) || ch is ';' or '|' or '&' or '(' or ')')
            {
                found |= token.ToString() == "--json";
                token.Clear();
            }
            else if (ch == '#' && token.Length == 0)
            {
                // A line comment is not an invocation argument.
                while (i + 1 < command.Length && command[i + 1] is not ('\r' or '\n')) i++;
            }
            else
                token.Append(ch);
        }
        return quote == '\0' && (found || token.ToString() == "--json");
    }

    internal static string SubstitutePlaceholders(
        string template,
        string profileName,
        string promptPath,
        string modelAlias,
        string? reasoningEffort,
        string workingDirectory,
        Action<string>? diagnosticSink = null)
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile(profileName);
        var permissionMode = provider.Identity.Kind == ProviderKind.AnthropicClaudeCli ? "default" : "plan";
        var resolvedTemplate = provider.Identity.Kind == ProviderKind.AnthropicClaudeCli
            ? ResolveClaudeEffortSegment(template, profileName, reasoningEffort, diagnosticSink)
            : template;
        return resolvedTemplate
            .Replace("{promptPath}", Quote(promptPath), StringComparison.OrdinalIgnoreCase)
            .Replace("{subscriptionModelName}", Quote(modelAlias), StringComparison.OrdinalIgnoreCase)
            .Replace("{subscriptionReasoningEffort}", Quote(string.IsNullOrWhiteSpace(reasoningEffort) ? AgentCatalog.ComplexReasoningEffort : reasoningEffort), StringComparison.OrdinalIgnoreCase)
            .Replace("{sandboxMode}", Quote("read-only"), StringComparison.OrdinalIgnoreCase)
            .Replace("{permissionMode}", Quote(permissionMode), StringComparison.OrdinalIgnoreCase)
            .Replace("{workingDirectory}", Quote(workingDirectory), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the Claude effort segment for the orchestrator-internal path, which has no dispatch
    /// preflight to refuse at. A configured value the CLI does not accept elides the flag and names
    /// itself in a diagnostic rather than hard-failing: refinement, review glance, and acceptance
    /// evaluation must keep running, and criterion 7 forbids turning this into a failed invocation.
    /// A custom template with no effort segment is left byte-for-byte alone and only warned about.
    /// </summary>
    private static string ResolveClaudeEffortSegment(
        string template,
        string profileName,
        string? reasoningEffort,
        Action<string>? diagnosticSink)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            // No configured policy: emit no flag so user-level Claude settings govern unchanged.
            return ClaudeCliEffortPolicy.ElideEffortSegment(template);
        }

        if (!ClaudeCliEffortPolicy.IsSupported(reasoningEffort))
        {
            Warn(
                diagnosticSink,
                $"unsupported:{profileName}:{reasoningEffort}",
                $"[SubscriptionCliCompleter] WARNING: worker profile '{profileName}' configured reasoning effort " +
                $"'{reasoningEffort}' is not supported by the Claude CLI; supported values: " +
                $"{ClaudeCliEffortPolicy.SupportedValuesDisplay}. The {ClaudeCliEffortPolicy.EffortFlag} argument was omitted " +
                "and the value was neither clamped nor remapped.");
            return ClaudeCliEffortPolicy.ElideEffortSegment(template);
        }

        // Invocation-time, in-memory repair of a superseded built-in: this call renders from the current
        // built-in so the configured policy reaches the CLI. The profile store is untouched - this completer
        // never writes profiles - so persisting the repair stays a separate explicit operator migration.
        var resolved = ClaudeCliEffortPolicy.ResolveInvocationCommandTemplate(profileName, template, diagnosticSink);
        if (!WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(resolved))
        {
            Warn(
                diagnosticSink,
                $"unmaterialized:{profileName}:{reasoningEffort}",
                $"[SubscriptionCliCompleter] WARNING: worker profile '{profileName}' has no " +
                $"{ClaudeCliEffortPolicy.EffortPlaceholder} template variable; configured reasoning effort " +
                $"'{reasoningEffort}' was not materialized. The saved command template was left unchanged.");
        }

        return resolved;
    }

    // The completer runs on every refinement, glance, and acceptance pass; without the guard a single
    // misconfigured profile would repeat the same line on every invocation. A supplied sink bypasses the
    // guard so a caller observing diagnostics never depends on which invocation happened to be first.
    private static readonly HashSet<string> _emittedWarnings = new(StringComparer.Ordinal);

    private static void Warn(Action<string>? diagnosticSink, string key, string message)
    {
        if (diagnosticSink is not null)
        {
            diagnosticSink(message);
            return;
        }

        lock (_emittedWarnings)
        {
            if (!_emittedWarnings.Add(key))
            {
                return;
            }
        }

        Console.Error.WriteLine(message);
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
            result.ExitCode == 0 ? null : $"subscription-cli-exit:{result.ExitCode}")
        {
            RawStandardOutput = result.StandardOutput
        };
    }
}
