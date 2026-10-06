using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IBoardFillPremiseVerifier
{
    Task<BoardFillPremiseVerification> VerifyAsync(string premise, string head, CancellationToken token);
}

internal sealed class BoardFillPremiseVerifier(Func<ModelFunctionCatalog> catalog,
    IAuthorBriefDraftRepository repository, string repositoryRoot,
    Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process = null) : IBoardFillPremiseVerifier
{
    public async Task<BoardFillPremiseVerification> VerifyAsync(string premise, string head, CancellationToken token)
    {
        var count = BoardFillVerifierContract.BulletCount(premise);
        BoardFillPremiseVerification Failure(string status, string detail) => new(status, count, [], detail);
        if (count == 0) return Failure("unavailable", "no-premise-bullets");
        BoardFillVerifierBinding binding;
        try { binding = BoardFillVerifierBinding.Resolve(catalog()); }
        catch (Exception exception) { return Failure("unavailable", "catalog:" + exception.GetType().Name); }
        if (binding.Error is { } error) return Failure("unavailable", error);
        try
        {
            if (!MatchesHead(head)) return Failure("failed", "repository-changed");
            var result = await (process ?? PanelJudgeProcess.RunAsync)(new(Command(binding), repositoryRoot,
                ConductorJudgePanelBudgets.JudgeTimeout, BoardFillVerifierContract.Prompt(premise, head)), token).ConfigureAwait(false);
            if (!MatchesHead(head)) return Failure("failed", "repository-changed");
            if (token.IsCancellationRequested) return Failure("failed", "cancelled");
            if (result.TimedOut) return Failure("failed", "timeout");
            if (result.Failure is { } failure) return Failure("failed", failure);
            if (result.ExitCode != 0) return Failure("failed", $"exit-code:{result.ExitCode?.ToString() ?? "missing"}");
            var (answer, providerFault) = Decode(result.Stdout, binding.Profile!);
            if (providerFault) return Failure("failed", "provider-fault");
            var verification = BoardFillVerifierContract.Parse(answer, count);
            // Evidence must resolve within a tracked file at the attested HEAD.
            foreach (var verdict in verification.Verdicts)
            {
                var citation = BoardFillCitation.Parse(verdict.Evidence);
                var lines = repository.TrackedLineCount(head, citation.Path);
                if (lines is null || !citation.InRange(lines.Value))
                    return Failure("invalid", $"invalid-evidence:{verdict.Bullet}") with
                    {
                        InvalidEvidence = $"{verdict.Evidence} ({(lines is null ? "untracked" : "line-outside")})"
                    };
            }
            return MatchesHead(head) ? verification : Failure("failed", "repository-changed");
        }
        catch (OperationCanceledException) { return Failure("failed", "cancelled"); }
        catch (Exception exception) { return Failure("failed", exception.GetType().Name); }
    }

    private bool MatchesHead(string head)
    {
        try { return repository.ResolveMainHead() == head; }
        catch (Exception) { return false; }
    }

    private string Command(BoardFillVerifierBinding binding) =>
        binding.Profile!.Equals(WorkerProfileDispatcher.OpenAiSubscriptionProfileName, StringComparison.OrdinalIgnoreCase)
            ? $"codex exec --skip-git-repo-check --model {Quote(binding.Alias!)}" +
                (string.IsNullOrWhiteSpace(binding.Effort) ? "" : $" -c {Quote("model_reasoning_effort=\"" + binding.Effort + "\"")}") +
                $" --sandbox read-only --cd {Quote(repositoryRoot)} -"
            : $"claude -p --model {Quote(binding.Alias!)} --permission-mode plan --tools 'Read,Grep,Glob' --no-session-persistence" +
                $" --output-format json --json-schema {Quote(BoardFillVerifierContract.JsonSchema)}";

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static (string Answer, bool ProviderFault) Decode(string stdout, string profile)
    {
        if (!profile.Equals(WorkerProfileDispatcher.AnthropicSubscriptionProfileName, StringComparison.OrdinalIgnoreCase))
            return (stdout, false);
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "result")
                return (stdout, false);
            var fault = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
            var answer = root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object
                ? structured.GetRawText() : root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                    ? result.GetString()! : stdout;
            return (answer, fault);
        }
        catch (JsonException) { return (stdout, false); }
    }
}
