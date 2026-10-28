using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorRoundInput(
    ConductorAuthorItem Item, string GoalBrief, string RefinedSpec,
    SpecRefinerPrecedent? MatchingPrecedent, ConductorLessonSelection? Lessons = null,
    int Attempt = 1, Func<bool>? TryConsumeFallbackAttempt = null);

internal interface IConductorAuthorModelRound
{
    string? ModelAlias => null;
    Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class ClaudeConductorAuthorModelRound(
    string receiptDirectory,
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null,
    ModelFunctionCatalog? catalog = null,
    Func<string, bool>? launcherAvailable = null)
    : IConductorAuthorModelRound
{
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync =
        runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
    private readonly Func<string, bool> _launcherAvailable = launcherAvailable ?? WorkerProfileDiagnostics.LocalCommandExists;

    public string? ModelAlias => ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.ConductorAuthor).Alias;

    public async Task<string> DispatchAsync(ConductorAuthorRoundInput input,
        string workingDirectory, CancellationToken cancellationToken)
    {
        var model = ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.ConductorAuthor);
        var roundId = Guid.NewGuid().ToString("N");
        if (!model.IsValid)
        {
            WriteReceipt(input, roundId, input.Attempt, model, null, model.InvalidReason, null, null, model.InvalidReason);
            throw new ConductorModelRoundException(model.InvalidReason!, null, model.InvalidReason);
        }
        var prompt = ConductorAuthorPrompt.Render(input);
        AuthorExhaustion? primaryExhaustion = null;
        string? primaryReceipt = null;
        for (var invocation = 0; invocation < 2; invocation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = input.Attempt + invocation;
            WorkerProcessRunResult result;
            try
            {
                // Keep the established Claude plan-mode contract. Codex uses the existing command
                // builder and authentication, with sandbox authority fixed here rather than configured.
                var command = model.WorkerProfileName == "codex-cli"
                    ? string.Join(" ", ProviderCommandBuilder.Build(ProviderKind.OpenAICodexCli, model.Alias!,
                        model.ReasoningEffort, "plan", "read-only", workingDirectory))
                    : $"claude {model.ModelArguments} --permission-mode plan -p";
                result = await _runProcessAsync(new WorkerProcessRunRequest(command,
                    workingDirectory, TimeSpan.FromMinutes(10), prompt), cancellationToken);
            }
            catch (Exception ex)
            {
                WriteReceipt(input, roundId, attempt, model, null, $"{ex.GetType().Name}: {ex.Message}",
                    primaryReceipt, primaryExhaustion, null);
                throw;
            }

            string? failure = result.TimedOut ? "author-timeout" : result.CaptureLimited ? "author-capture-limited" :
                result.ExitCode != 0 ? $"Author model exited {result.ExitCode}: {result.StandardError}" : null;
            var answer = result.StandardOutput;
            if (failure is null && model.WorkerProfileName == "codex-cli" &&
                !CodexJsonlUsageParser.TryParseCompletedAnswer(result.StandardOutput, out answer, out var transportFailure))
                failure = transportFailure;

            var exhaustion = Exhaustion(result, model.WorkerProfileName);
            if (invocation == 0) primaryExhaustion = exhaustion;
            ConductorRoundModel? alternate = null;
            string? terminal = null;
            try
            {
                if (exhaustion is not null)
                {
                    if (invocation != 0 || attempt >= ConductorAuthorHost.MaxAttempts)
                        terminal = "author-attempt-budget-exhausted";
                    else
                    {
                        alternate = ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.ConductorAuthor, fallback: true);
                        if (!alternate.IsValid) terminal = alternate.InvalidReason;
                        else if (alternate.WorkerProfileName == model.WorkerProfileName)
                            terminal = "author-fallback-same-provider";
                        else if (!_launcherAvailable(alternate.WorkerProfileName == "codex-cli" ? "codex" : "claude"))
                            terminal = $"author-fallback-unavailable:{alternate.WorkerProfileName}";
                        else if (input.TryConsumeFallbackAttempt?.Invoke() != true)
                            terminal = "author-fallback-attempt-not-reserved";
                    }
                }
            }
            catch (Exception ex)
            {
                // Selection/reservation failure cannot discard the provider receipt or create
                // another retry allowance when durable consumption may already have occurred.
                terminal = $"author-fallback-selection-failed:{ex.GetType().Name}";
                WriteReceipt(input, roundId, attempt, model, result, failure,
                    primaryReceipt, primaryExhaustion, terminal, exhaustion);
                throw new ConductorModelRoundException(terminal, model.Alias, terminal);
            }

            var receipt = WriteReceipt(input, roundId, attempt, model, result, failure,
                primaryReceipt, primaryExhaustion, terminal, exhaustion);
            if (failure is null) return answer;
            if (exhaustion is null || terminal is not null)
                throw new ConductorModelRoundException(failure, model.Alias, terminal);
            primaryReceipt = receipt;
            model = alternate!;
        }
        throw new InvalidOperationException("Author invocation bound was exceeded.");
    }

    private static AuthorExhaustion? Exhaustion(WorkerProcessRunResult result, string launcher)
    {
        if (result.ExitCode == 0 || result.TimedOut || result.CaptureLimited) return null;
        // Only provider diagnostics and whole-invocation refusal stdout are authoritative.
        // Never classify the prompt, source excerpts, or decoded assistant messages.
        if (ProviderLimitEvidenceParser.TryGetEvidenceLine(null, result.StandardError, out var diagnostic))
            return new(launcher, "stderr-diagnostic", diagnostic);
        var refusal = ProviderLimitEvidenceParser.GetCliRefusalLines(result.ExitCode,
            result.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r')))
            .FirstOrDefault(ProviderLimitEvidenceParser.IsCliRefusalLimitLine);
        return refusal is null ? null : new(launcher, "stdout-refusal", refusal);
    }

    private string WriteReceipt(ConductorAuthorRoundInput input, string roundId, int attempt,
        ConductorRoundModel identity, WorkerProcessRunResult? result, string? failure,
        string? primaryReceipt, AuthorExhaustion? primaryExhaustion, string? terminalReason,
        AuthorExhaustion? exhaustion = null)
    {
        Directory.CreateDirectory(receiptDirectory);
        var path = Path.Combine(receiptDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            item = input.Item.Identity, completedAt = DateTimeOffset.UtcNow,
            exitCode = result?.ExitCode, stdout = result?.StandardOutput, stderr = result?.StandardError,
            failure, model = identity.Alias, lessonIds = input.Lessons?.LessonIds.ToArray() ?? [],
            launcher = identity.IsValid ? identity.WorkerProfileName : null, reasoningEffort = identity.ReasoningEffort,
            roundId, attempt, primaryReceipt, primaryExhaustion, exhaustion, terminalReason,
            timedOut = result?.TimedOut, captureLimited = result?.CaptureLimited
        }));
        return path;
    }

    private sealed record AuthorExhaustion(string Launcher, string Source, string Diagnostic);
}
