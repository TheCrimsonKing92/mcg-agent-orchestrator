using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record InquiryDispatchRequest(
    Goal Goal,
    TaskSpec Task,
    string Question,
    string PromptDirectory,
    string ReceiptDirectory,
    string GoalLifecycleEventsDirectory,
    InquiryAdmissionOptions? AdmissionOptions = null);

public sealed record InquiryCommandPlan(
    string ParentDispatchId,
    string PromptPath,
    string PromptContent,
    string Command,
    InquiryExecutionMode ExecutionMode,
    InquiryAdmissionDecision Admission,
    string? ParentSessionId);

public sealed record InquiryDispatchResult(InquiryAnswerReceipt Receipt, string ReceiptPath);

public sealed class InquiryDispatcher
{
    public const string FreshnessEnvelopeHeader = "FRESHNESS ENVELOPE";
    private const int MaxQuestionCharacters = 20000;

    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync;
    private readonly IClock _clock;
    private readonly Func<string, string?> _headResolver;
    private readonly Func<string, string?, string?, bool> _capturedHeadIsAncestor;

    public InquiryDispatcher(
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null,
        IClock? clock = null,
        Func<string, string?>? headResolver = null,
        Func<string, string?, string?, bool>? capturedHeadIsAncestor = null)
    {
        _runProcessAsync = runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
        _clock = clock ?? new SystemClock();
        _headResolver = headResolver ?? TryResolveHead;
        _capturedHeadIsAncestor = capturedHeadIsAncestor ?? IsAncestor;
    }

    public InquiryCommandPlan Compose(InquiryDispatchRequest request)
    {
        var dispatch = RequireCompletedDispatch(request.Task);
        var providerKind = ResolveProviderKind(dispatch);
        var worktree = dispatch.WorkingDirectory;
        var now = _clock.UtcNow;
        var currentHead = _headResolver(worktree);
        var capturedHeadIsAncestor = _capturedHeadIsAncestor(worktree, dispatch.WorktreeHeadSha, currentHead);
        var store = new InquiryReceiptStore(request.ReceiptDirectory);
        var parentDispatchId = BuildParentDispatchId(request.Goal.Id, request.Task.Id, dispatch);
        var parentSessionId = Normalize(dispatch.ProviderSessionId);
        var context = new InquiryAdmissionContext(
            request.Goal.Id,
            request.Task.Id,
            request.Task.RequiredRole,
            providerKind,
            dispatch.ModelName,
            worktree,
            currentHead,
            capturedHeadIsAncestor,
            now,
            LatestCriteriaCorrectionAfter(request.Goal, dispatch.DispatchedAt),
            LatestIntegrationChangeAfter(request.GoalLifecycleEventsDirectory, request.Goal.Id, dispatch.DispatchedAt),
            parentSessionId is not null && store.HasNonForkedResumeInquiry(parentDispatchId, parentSessionId),
            parentSessionId is null ? 0 : store.CountSessionTurns(parentDispatchId, parentSessionId));
        var admission = InquiryResumeAdmission.Evaluate(
            request.Goal,
            request.Task,
            dispatch,
            context,
            request.AdmissionOptions);
        var executionMode = admission.AllowsResume
            ? providerKind == ProviderKind.AnthropicClaudeCli
                ? InquiryExecutionMode.ForkedResume
                : InquiryExecutionMode.Resume
            : InquiryExecutionMode.FreshExec;
        var promptContent = BuildPrompt(request, dispatch, admission, executionMode);
        var promptPath = WritePrompt(request, promptContent, now);
        var command = BuildCommand(providerKind, dispatch, worktree, parentSessionId, executionMode);
        ThrowIfForbiddenResumeLast(command);
        return new InquiryCommandPlan(
            parentDispatchId,
            promptPath,
            promptContent,
            command,
            executionMode,
            admission,
            parentSessionId);
    }

    public async Task<InquiryDispatchResult> DispatchAsync(InquiryDispatchRequest request, CancellationToken cancellationToken = default)
    {
        var plan = Compose(request);
        var stopwatch = Stopwatch.StartNew();
        var result = await _runProcessAsync(
            new WorkerProcessRunRequest(
                plan.Command,
                request.Task.LastDispatch!.WorkingDirectory,
                Timeout: TimeSpan.FromMinutes(20),
                StandardInput: plan.PromptContent),
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var completedAt = _clock.UtcNow;
        var receiptId = Guid.NewGuid().ToString("N");
        var transcriptPath = WriteTranscript(request.ReceiptDirectory, receiptId, plan, result, completedAt);
        var forkedSessionId = TryCaptureProviderSessionId(result.StandardOutput) ??
            TryCaptureProviderSessionId(result.StandardError);
        var receipt = new InquiryAnswerReceipt(
            InquiryAnswerReceipt.InquiryKind,
            InquiryAnswerReceipt.PostHocClaimsLabel,
            receiptId,
            plan.ParentDispatchId,
            request.Goal.Id,
            request.Task.Id,
            request.Task.LastDispatch!.WorkerName,
            ResolveProviderKind(request.Task.LastDispatch),
            plan.ExecutionMode,
            plan.ParentSessionId,
            plan.ExecutionMode == InquiryExecutionMode.ForkedResume ? forkedSessionId : null,
            BoundQuestion(request.Question),
            transcriptPath,
            ParseUsage(result.StandardOutput + Environment.NewLine + result.StandardError),
            (long)stopwatch.Elapsed.TotalMilliseconds,
            plan.Admission,
            plan.Command,
            result.ExitCode,
            completedAt);
        var receiptPath = new InquiryReceiptStore(request.ReceiptDirectory).Save(receipt);
        return new InquiryDispatchResult(receipt, receiptPath);
    }

    public static string BuildParentDispatchId(GoalId goalId, TaskId taskId, TaskDispatchRecord dispatch)
    {
        var raw = string.Join('\u001f',
            goalId.Value,
            taskId.Value,
            dispatch.DispatchedAt.ToUniversalTime().ToString("O"),
            dispatch.WorkerName,
            dispatch.WorkingDirectory,
            dispatch.ProviderSessionId,
            dispatch.Command);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    internal static void ThrowIfForbiddenResumeLast(string command)
    {
        if (Regex.IsMatch(command, @"(^|\s)--last(\s|$)", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Inquiry command composition must never use --last-based resumption.");
        }
    }

    private static TaskDispatchRecord RequireCompletedDispatch(TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Completed)
        {
            throw new InvalidOperationException($"Inquiry targets completed dispatches only; task '{task.Id}' status is {task.Status}.");
        }

        if (task.LastDispatch is null)
        {
            throw new InvalidOperationException($"Task '{task.Id}' has no completed dispatch to inquire against.");
        }

        return task.LastDispatch;
    }

    private static string BuildPrompt(
        InquiryDispatchRequest request,
        TaskDispatchRecord dispatch,
        InquiryAdmissionDecision admission,
        InquiryExecutionMode executionMode)
    {
        var lines = new List<string>();
        if (executionMode is InquiryExecutionMode.Resume or InquiryExecutionMode.ForkedResume)
        {
            lines.Add(FreshnessEnvelopeHeader + ": current artifacts in this prompt are authoritative over any remembered session context. If memory conflicts with this envelope, ignore memory.");
            lines.Add(string.Empty);
        }

        lines.Add("# Worker Inquiry");
        lines.Add("This is an inquiry against a completed worker dispatch.");
        lines.Add("Treat your answer as POST-HOC CLAIMS: it informs operator judgment but does not mutate task status, verification, or acceptance state.");
        lines.Add(string.Empty);
        lines.Add($"Goal: {request.Goal.Id.Value}");
        lines.Add($"Task: {request.Task.Id.Value}");
        lines.Add($"Role: {request.Task.RequiredRole}");
        lines.Add($"Parent dispatch: {dispatch.WorkerName} at {dispatch.DispatchedAt:u}");
        lines.Add($"Admission outcome: {admission.Outcome}");
        if (!admission.AllowsResume)
        {
            lines.Add($"Fresh fallback reason: {admission.Reason}");
        }

        lines.Add(string.Empty);
        lines.Add("## Question");
        lines.Add(BoundQuestion(request.Question));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string WritePrompt(InquiryDispatchRequest request, string promptContent, DateTimeOffset now)
    {
        Directory.CreateDirectory(request.PromptDirectory);
        var promptPath = Path.Combine(
            request.PromptDirectory,
            $"{ShortId(request.Goal.Id.Value)}-{ShortId(request.Task.Id.Value)}-{now:yyyyMMddHHmmss}-inquiry.md");
        File.WriteAllText(promptPath, promptContent, new UTF8Encoding(false));
        return promptPath;
    }

    private static string BuildCommand(
        ProviderKind providerKind,
        TaskDispatchRecord dispatch,
        string worktree,
        string? sessionId,
        InquiryExecutionMode executionMode)
    {
        return providerKind switch
        {
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark => BuildCodexCommand(dispatch, worktree, sessionId, executionMode),
            ProviderKind.AnthropicClaudeCli => BuildClaudeCommand(dispatch, sessionId, executionMode),
            _ => throw new InvalidOperationException($"Inquiry supports codex and claude subscription lanes only; dispatch provider was {providerKind}.")
        };
    }

    private static string BuildCodexCommand(TaskDispatchRecord dispatch, string worktree, string? sessionId, InquiryExecutionMode executionMode)
    {
        var model = string.IsNullOrWhiteSpace(dispatch.ModelName) ? string.Empty : $" --model {Quote(dispatch.ModelName)}";
        var reasoning = string.IsNullOrWhiteSpace(dispatch.ReasoningEffort) ? string.Empty : $" -c model_reasoning_effort={Quote(dispatch.ReasoningEffort)}";
        return executionMode == InquiryExecutionMode.Resume
            ? $"codex exec --skip-git-repo-check{model}{reasoning} --sandbox read-only --cd {Quote(worktree)} resume {Quote(sessionId ?? throw new InvalidOperationException("Resume inquiry requires parent session id."))} -"
            : $"codex exec --skip-git-repo-check{model}{reasoning} --sandbox read-only --cd {Quote(worktree)}";
    }

    private static string BuildClaudeCommand(TaskDispatchRecord dispatch, string? sessionId, InquiryExecutionMode executionMode)
    {
        var model = string.IsNullOrWhiteSpace(dispatch.ModelName) ? string.Empty : $" --model {Quote(dispatch.ModelName)}";
        return executionMode == InquiryExecutionMode.ForkedResume
            ? $"claude{model} --permission-mode plan -p --resume {Quote(sessionId ?? throw new InvalidOperationException("Forked resume inquiry requires parent session id."))} --fork-session"
            : $"claude{model} --permission-mode plan -p";
    }

    private static string WriteTranscript(
        string receiptDirectory,
        string receiptId,
        InquiryCommandPlan plan,
        WorkerProcessRunResult result,
        DateTimeOffset completedAt)
    {
        Directory.CreateDirectory(receiptDirectory);
        var path = Path.Combine(receiptDirectory, $"{receiptId}.transcript.md");
        var lines = new List<string>
        {
            "# Inquiry Transcript",
            string.Empty,
            $"Completed: {completedAt:u}",
            $"Claims label: {InquiryAnswerReceipt.PostHocClaimsLabel}",
            $"Execution mode: {plan.ExecutionMode}",
            $"Admission: {plan.Admission.Outcome}",
            $"Exit code: {result.ExitCode}",
            string.Empty,
            "## Standard Output",
            result.StandardOutput,
            string.Empty,
            "## Standard Error",
            result.StandardError
        };
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
        return path;
    }

    private static ProviderKind ResolveProviderKind(TaskDispatchRecord dispatch)
    {
        if (dispatch.WorkerProviderKind != ProviderKind.Unknown)
        {
            return dispatch.WorkerProviderKind;
        }

        return dispatch.WorkerName.Equals(WorkerProfileDispatcher.AnthropicSubscriptionProfileName, StringComparison.OrdinalIgnoreCase)
            ? ProviderKind.AnthropicClaudeCli
            : dispatch.WorkerName.Equals(WorkerProfileDispatcher.OpenAiSubscriptionProfileName, StringComparison.OrdinalIgnoreCase) ||
              dispatch.WorkerName.Equals(WorkerProfileDispatcher.OpenAiSparkSubscriptionProfileName, StringComparison.OrdinalIgnoreCase)
                ? ProviderKind.OpenAICodexCli
                : ProviderKind.Unknown;
    }

    private static DateTimeOffset? LatestCriteriaCorrectionAfter(Goal goal, DateTimeOffset cutoff)
    {
        var latest = goal.EffectiveAcceptanceCriteriaCorrections
            .Where(correction => correction.RecordedAt > cutoff)
            .OrderByDescending(correction => correction.RecordedAt)
            .FirstOrDefault();
        return latest?.RecordedAt;
    }

    private static DateTimeOffset? LatestIntegrationChangeAfter(string eventsDirectory, GoalId goalId, DateTimeOffset cutoff)
    {
        var path = Path.Combine(eventsDirectory, $"{goalId.Value}.jsonl");
        if (!File.Exists(path))
        {
            return null;
        }

        DateTimeOffset? latest = null;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = root.TryGetProperty("eventType", out var eventTypeProperty)
                    ? eventTypeProperty.GetString()
                    : null;
                if (!IsIntegrationChangeEvent(eventType, root))
                {
                    continue;
                }

                if (root.TryGetProperty("timestamp", out var timestampProperty) &&
                    timestampProperty.TryGetDateTimeOffset(out var timestamp) &&
                    timestamp > cutoff &&
                    (latest is null || timestamp > latest))
                {
                    latest = timestamp;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return latest;
    }

    private static bool IsIntegrationChangeEvent(string? eventType, JsonElement root)
    {
        if (string.Equals(eventType, "GoalLanded", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(eventType, "GoalEscalated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return root.TryGetProperty("source", out var source) &&
            source.ValueKind == JsonValueKind.String &&
            source.GetString()?.Contains("integration", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? TryResolveHead(string workingDirectory) =>
        TryReadGit(workingDirectory, "rev-parse", "HEAD");

    private static string? TryReadGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.Output)
            ? result.Output.Trim()
            : null;
    }

    private static bool IsAncestor(string workingDirectory, string? capturedHead, string? currentHead)
    {
        if (string.IsNullOrWhiteSpace(capturedHead) || string.IsNullOrWhiteSpace(currentHead))
        {
            return false;
        }

        if (string.Equals(capturedHead.Trim(), currentHead.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GitCli.Run(workingDirectory, "merge-base", "--is-ancestor", capturedHead.Trim(), currentHead.Trim()).Succeeded;
    }

    private static string? TryCaptureProviderSessionId(string text)
    {
        foreach (var line in ReadLines(text))
        {
            if (DispatchProcessHost.TryCaptureProviderSessionIdFromLine(line, out var sessionId))
            {
                return sessionId;
            }
        }

        return null;
    }

    private static InquiryTokenUsage ParseUsage(string text)
    {
        return new InquiryTokenUsage(
            TryMatchInt(text, @"\binput(?:_|\s+)?tokens\b\s*[:=]\s*(\d+)"),
            TryMatchInt(text, @"\bcached(?:_|\s+)?(?:input(?:_|\s+)?)?tokens\b\s*[:=]\s*(\d+)"),
            TryMatchInt(text, @"\boutput(?:_|\s+)?tokens\b\s*[:=]\s*(\d+)"));
    }

    private static int? TryMatchInt(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    private static IEnumerable<string> ReadLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string BoundQuestion(string question)
    {
        question = string.IsNullOrWhiteSpace(question)
            ? throw new ArgumentException("Inquiry question cannot be empty.", nameof(question))
            : question.Trim();
        return question.Length <= MaxQuestionCharacters
            ? question
            : question[..MaxQuestionCharacters] + Environment.NewLine + $"...[truncated {question.Length - MaxQuestionCharacters} chars]...";
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ShortId(string value) => value.Length <= 8 ? value : value[..8];

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

public sealed class InquiryReceiptStore(string receiptDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Save(InquiryAnswerReceipt receipt)
    {
        Directory.CreateDirectory(receiptDirectory);
        var path = Path.Combine(receiptDirectory, $"{receipt.ReceiptId}.receipt.json");
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, JsonOptions), new UTF8Encoding(false));
        return path;
    }

    public bool HasNonForkedResumeInquiry(string parentDispatchId, string parentSessionId) =>
        EnumerateReceipts()
            .Any(receipt =>
                receipt.ParentDispatchId == parentDispatchId &&
                string.Equals(receipt.ParentSessionId, parentSessionId, StringComparison.Ordinal) &&
                receipt.ExecutionMode == InquiryExecutionMode.Resume);

    public int CountSessionTurns(string parentDispatchId, string parentSessionId) =>
        1 + EnumerateReceipts()
            .Count(receipt =>
                receipt.ParentDispatchId == parentDispatchId &&
                string.Equals(receipt.ParentSessionId, parentSessionId, StringComparison.Ordinal) &&
                receipt.ExecutionMode == InquiryExecutionMode.Resume);

    private IEnumerable<InquiryReceiptProjection> EnumerateReceipts()
    {
        if (!Directory.Exists(receiptDirectory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(receiptDirectory, "*.receipt.json"))
        {
            InquiryReceiptProjection? receipt = null;
            try
            {
                receipt = JsonSerializer.Deserialize<InquiryReceiptProjection>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException)
            {
            }

            if (receipt is not null)
            {
                yield return receipt;
            }
        }
    }

    private sealed record InquiryReceiptProjection(
        string ParentDispatchId,
        string? ParentSessionId,
        InquiryExecutionMode ExecutionMode);
}
