using Mcg.AgentOrchestrator.Core;
using System.Diagnostics;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerPromptInputBudgetExceededException : InvalidOperationException
{
    public WorkerPromptInputBudgetExceededException(
        GoalId goalId,
        TaskId taskId,
        string providerName,
        string modelName,
        int tokenCount,
        int tokenBudget)
        : base(
            $"Assembled worker prompt for task '{taskId.Value}' is {tokenCount:N0} tokens, " +
            $"exceeding the {tokenBudget:N0}-token input budget for {providerName}/{modelName}; " +
            "required context is lossless and cannot be removed before dispatch. Shorten the authoritative brief or use mandatory-file delivery before redispatching.")
    {
        GoalId = goalId;
        TaskId = taskId;
        ProviderName = providerName;
        ModelName = modelName;
        TokenCount = tokenCount;
        TokenBudget = tokenBudget;
    }

    public GoalId GoalId { get; }

    public TaskId TaskId { get; }

    public string ProviderName { get; }

    public string ModelName { get; }

    public int TokenCount { get; }

    public int TokenBudget { get; }
}

public sealed record WorkerPromptInputBudgetResult(
    TaskBrief Brief,
    int OriginalTokenCount,
    int TokenCount,
    int TokenBudget,
    IReadOnlyList<string> DroppedSections)
{
    public bool Trimmed => DroppedSections.Count > 0;
}

public static class WorkerPromptInputBudget
{
    public const int ReservedOutputTokens = 4096;

    private const int DefaultContextWindowTokens = 100_000;
    private const int AnthropicContextWindowTokens = 200_000;
    private const int OpenAiContextWindowTokens = 200_000;
    private const int OllamaContextWindowTokens = 8_192;
    private const int ApproximateCharactersPerToken = 4;
    private static readonly IReadOnlyList<ContextSectionRule> DropRules =
    [
        new("evidence", ["Prior Task Evidence", "Prior Task Handoff", "Last Verification", "Last Model Output", "Last Dispatch"]),
        new("source survey", ["Source Survey", "Dashboard Source Survey"]),
        new("digest", ["Worker Context Digest", "Context Digest"])
    ];

    public static WorkerPromptInputBudgetResult Apply(
        TaskBrief brief,
        string? providerName,
        string? modelName,
        int? inputTokenBudgetOverride = null,
        string? workerProfileName = null)
    {
        var provider = string.IsNullOrWhiteSpace(providerName) ? "unknown" : providerName.Trim();
        var model = string.IsNullOrWhiteSpace(modelName) ? "unknown" : modelName.Trim();
        var tokenBudget = inputTokenBudgetOverride ?? InputTokenBudget(provider, model, workerProfileName);
        var originalTokenCount = CountTokens(brief.Content);
        if (originalTokenCount <= tokenBudget)
        {
            return new WorkerPromptInputBudgetResult(brief, originalTokenCount, originalTokenCount, tokenBudget, []);
        }

        if (brief.Content.StartsWith("## Worker Context Package", StringComparison.Ordinal))
        {
            throw new WorkerPromptInputBudgetExceededException(
                brief.GoalId,
                brief.TaskId,
                provider,
                model,
                originalTokenCount,
                tokenBudget);
        }

        var content = brief.Content;
        var droppedSections = new List<string>();
        foreach (var rule in DropRules)
        {
            content = DropSections(content, rule.Headings, out var droppedCount);
            if (droppedCount == 0)
            {
                continue;
            }

            droppedSections.Add(rule.Name);
            var tokenCount = CountTokens(content);
            if (tokenCount <= tokenBudget)
            {
                LogTrim(brief.GoalId, brief.TaskId, originalTokenCount, tokenCount, droppedSections);
                return new WorkerPromptInputBudgetResult(
                    brief with { Content = content }, originalTokenCount, tokenCount, tokenBudget, droppedSections);
            }
        }

        var finalTokenCount = CountTokens(content);
        LogTrim(brief.GoalId, brief.TaskId, originalTokenCount, finalTokenCount, droppedSections);
        if (finalTokenCount > tokenBudget)
        {
            throw new WorkerPromptInputBudgetExceededException(
                brief.GoalId, brief.TaskId, provider, model, finalTokenCount, tokenBudget);
        }

        return new WorkerPromptInputBudgetResult(
            brief with { Content = content }, originalTokenCount, finalTokenCount, tokenBudget, droppedSections);
    }

    public static int CountTokens(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }

        return (content.Length + ApproximateCharactersPerToken - 1) / ApproximateCharactersPerToken;
    }

    public static int InputTokenBudget(
        string? providerName,
        string? modelName,
        string? workerProfileName = null)
    {
        var contextWindow = ContextWindowTokens(providerName, modelName);
        return Math.Max(
            1,
            contextWindow - ReservedOutputTokens - HarnessReservedTokens(workerProfileName));
    }

    public static int HarnessReservedTokens(string? workerProfileName)
    {
        if (string.IsNullOrWhiteSpace(workerProfileName))
        {
            return 0;
        }

        return workerProfileName.Trim().Equals(WorkerProfile.QwenCodeCliName, StringComparison.OrdinalIgnoreCase)
            ? WorkerProfile.QwenCodeBareStartupTokens
            : 0;
    }

    public static int ContextWindowTokens(string? providerName, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return DefaultContextWindowTokens;
        }

        return providerName.Trim() switch
        {
            var provider when provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) => AnthropicContextWindowTokens,
            var provider when provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) => OpenAiContextWindowTokens,
            var provider when provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) => OllamaContextWindowTokens,
            var provider when provider.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase) => LlamaCppDefaults.ContextWindowTokens,
            _ => DefaultContextWindowTokens
        };
    }

    private static string DropSections(string content, IReadOnlySet<string> headings, out int droppedCount)
    {
        var output = new StringBuilder(content.Length);
        droppedCount = 0;
        var dropping = false;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (TryGetSecondLevelHeading(line, out var heading))
            {
                dropping = headings.Contains(heading);
                if (dropping)
                {
                    droppedCount++;
                    continue;
                }
            }

            if (!dropping)
            {
                output.AppendLine(line);
            }
        }

        return output.ToString().TrimEnd('\r', '\n');
    }

    private static bool TryGetSecondLevelHeading(string line, out string heading)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("## ", StringComparison.Ordinal) || trimmed.StartsWith("### ", StringComparison.Ordinal))
        {
            heading = string.Empty;
            return false;
        }

        heading = trimmed[3..].Trim();
        return true;
    }

    private static void LogTrim(
        GoalId goalId,
        TaskId taskId,
        int originalTokenCount,
        int tokenCount,
        IReadOnlyList<string> droppedSections) => Trace.TraceInformation(
            "Worker prompt context trimmed for goal {0}, task {1}: originalTokens={2}, trimmedTokens={3}, droppedSections={4}",
            goalId.Value,
            taskId.Value,
            originalTokenCount,
            tokenCount,
            string.Join(", ", droppedSections));

    private sealed record ContextSectionRule(string Name, IReadOnlySet<string> Headings)
    {
        public ContextSectionRule(string name, IReadOnlyList<string> headings)
            : this(name, headings.ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
        }
    }

}
