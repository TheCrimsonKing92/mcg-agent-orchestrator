using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorRoundInput(
    ConductorAuthorItem Item, string GoalBrief, string RefinedSpec,
    SpecRefinerPrecedent? MatchingPrecedent, ConductorLessonSelection? Lessons = null);

internal interface IConductorAuthorModelRound
{
    Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class ClaudeConductorAuthorModelRound(
    string receiptDirectory,
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null)
    : IConductorAuthorModelRound
{
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync =
        runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;

    public async Task<string> DispatchAsync(ConductorAuthorRoundInput input,
        string workingDirectory, CancellationToken cancellationToken)
    {
        var prompt = ConductorAuthorPrompt.Render(input);
        WorkerProcessRunResult result;
        try
        {
            result = await _runProcessAsync(
                new WorkerProcessRunRequest("claude --model sonnet --permission-mode plan -p",
                    workingDirectory, TimeSpan.FromMinutes(10), prompt), cancellationToken);
            WriteReceipt(input.Item, result.ExitCode, result.StandardOutput, result.StandardError, null);
        }
        catch (Exception ex)
        {
            WriteReceipt(input.Item, null, null, null, $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Author model exited {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput;
    }

    private void WriteReceipt(ConductorAuthorItem item, int? exitCode,
        string? stdout, string? stderr, string? failure)
    {
        Directory.CreateDirectory(receiptDirectory);
        var path = Path.Combine(receiptDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            item = item.Identity, completedAt = DateTimeOffset.UtcNow,
            exitCode, stdout, stderr, failure
        }));
    }
}
