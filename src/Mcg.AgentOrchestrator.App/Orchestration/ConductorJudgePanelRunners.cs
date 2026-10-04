using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorPanelJudgeRunner
{
    string Judge { get; }
    string? BindingError { get; }
    Task<PanelJudgeResult> RunAsync(PanelCase item, CancellationToken cancellationToken);
}

internal sealed record PanelJudgeBinding(string? Alias, string? Effort, string? Error);
internal static class PanelJudgeBindingResolver
{
    internal static PanelJudgeBinding Resolve(ModelFunctionCatalog catalog, string purpose)
    {
        if (catalog.Bindings is null || catalog.Bindings.Any(binding => binding is null))
            return new(null, null, $"panel-binding-invalid:{purpose} binding=catalog cause=malformed-catalog");
        var expected = purpose switch
        {
            ModelFunctionPurposes.PanelJudgeSol => WorkerProfileDispatcher.OpenAiSubscriptionProfileName,
            ModelFunctionPurposes.PanelJudgeSonnet => WorkerProfileDispatcher.AnthropicSubscriptionProfileName,
            _ => null
        };
        var bindings = catalog.ForPurpose(purpose);
        var subscription = bindings.Count == 1 ? bindings[0].Subscription : null;
        var cause = expected is null ? "unknown-purpose" : bindings.Count != 1 ? "expected-one-binding" :
            subscription is null ? "missing-subscription" :
            !string.Equals(subscription.WorkerProfileName, expected, StringComparison.OrdinalIgnoreCase) ? "wrong-profile" :
            string.IsNullOrWhiteSpace(subscription.ModelAlias) ? "empty-model-alias" :
            subscription.ModelAlias.Any(char.IsControl) ? "malformed-model-alias" :
            subscription.ReasoningEffort is { Length: > 0 } effort && effort is not
                ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ? "invalid-effort" : null;
        return cause is null ? new(subscription!.ModelAlias!.Trim(), subscription.ReasoningEffort, null) :
            new(null, null, $"panel-binding-invalid:{purpose} binding={string.Join(',', bindings.Select(binding => binding.Name ?? binding.Purpose))} cause={cause}");
    }
}

internal abstract class ConductorPanelJudgeRunner : IConductorPanelJudgeRunner
{
    protected readonly PanelJudgeBinding Binding;
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>> _process;
    protected ConductorPanelJudgeRunner(ModelFunctionCatalog catalog, string purpose,
        Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process)
    {
        Binding = PanelJudgeBindingResolver.Resolve(catalog, purpose);
        _process = process ?? PanelJudgeProcess.RunAsync;
    }
    public abstract string Judge { get; }
    public string? BindingError => Binding.Error;
    protected abstract string Command(string directory);
    protected virtual (string Answer, string? Usage, bool ProviderFault) Decode(string stdout, string stderr) => (stdout, null, false);
    protected static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public async Task<PanelJudgeResult> RunAsync(PanelCase item, CancellationToken cancellationToken)
    {
        if (Binding.Error is { } invalid)
            return new(Judge, PanelJudgeOutcome.Skipped, null, "", "", Reason: invalid);
        var directory = Path.Combine(OrchestratorTempRoot.GetRoot(), "mcg-panel-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var result = await _process(new(Command(directory), directory,
                ConductorJudgePanelBudgets.JudgeTimeout, PanelV0Contract.Prompt(item)), cancellationToken).ConfigureAwait(false);
            var decoded = Decode(result.Stdout, result.Stderr);
            var outcome = result.TimedOut ? PanelJudgeOutcome.TimedOut :
                result.Failure is not null || result.ExitCode != 0 || decoded.ProviderFault ? PanelJudgeOutcome.InvocationFailed :
                PanelV0Contract.Validate(decoded.Answer, item.Id);
            return new(Judge, outcome, result.ExitCode, result.Stdout, result.Stderr, decoded.Answer,
                result.Failure, decoded.Usage, Binding.Alias);
        }
        catch (OperationCanceledException)
        { return new(Judge, PanelJudgeOutcome.TimedOut, null, "", "", Reason: "cancelled", ModelAlias: Binding.Alias); }
        catch (Exception exception)
        { return new(Judge, PanelJudgeOutcome.InvocationFailed, null, "", "", Reason: exception.Message, ModelAlias: Binding.Alias); }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class CodexSolPanelJudgeRunner(ModelFunctionCatalog catalog,
    Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process = null)
    : ConductorPanelJudgeRunner(catalog, ModelFunctionPurposes.PanelJudgeSol, process)
{
    public override string Judge => "sol";
    protected override string Command(string directory) =>
        $"codex exec --skip-git-repo-check --model {Quote(Binding.Alias!)}" +
        (string.IsNullOrWhiteSpace(Binding.Effort) ? "" : $" -c {Quote("model_reasoning_effort=\"" + Binding.Effort + "\"")}") +
        $" --sandbox read-only --cd {Quote(directory)} -";
    protected override (string Answer, string? Usage, bool ProviderFault) Decode(string stdout, string stderr)
    {
        var match = Regex.Match(stderr, @"(?im)^tokens used\s*\r?\n([\d,]+)\s*$");
        var usage = match.Success && long.TryParse(match.Groups[1].Value.Replace(",", ""), out var tokens)
            ? JsonSerializer.Serialize(new { total_tokens = tokens }) : null;
        return (stdout, usage, false);
    }
}

internal sealed class ClaudeSonnetPanelJudgeRunner(ModelFunctionCatalog catalog,
    Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process = null)
    : ConductorPanelJudgeRunner(catalog, ModelFunctionPurposes.PanelJudgeSonnet, process)
{
    public override string Judge => "sonnet";
    protected override string Command(string directory) =>
        $"claude -p --model {Quote(Binding.Alias!)} --restricted --tools '' --no-session-persistence" +
        $" --output-format json --json-schema {Quote(PanelV0Contract.JsonSchema)}";
    protected override (string Answer, string? Usage, bool ProviderFault) Decode(string stdout, string stderr)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "result")
                return (stdout, null, false);
            var usage = root.TryGetProperty("usage", out var used) ? used.GetRawText() : null;
            var fault = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
            var answer = root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object
                ? structured.GetRawText() : root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                    ? result.GetString()! : stdout;
            return (answer, usage, fault);
        }
        catch (JsonException) { return (stdout, null, false); }
    }
}
