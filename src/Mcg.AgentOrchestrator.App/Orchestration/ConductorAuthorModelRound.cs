using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorRoundInput(
    ConductorAuthorItem Item, string GoalBrief, string RefinedSpec,
    SpecRefinerPrecedent? MatchingPrecedent);

internal interface IConductorAuthorModelRound
{
    Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class ClaudeConductorAuthorModelRound(string receiptDirectory) : IConductorAuthorModelRound
{
    public async Task<string> DispatchAsync(ConductorAuthorRoundInput input,
        string workingDirectory, CancellationToken cancellationToken)
    {
        var prompt = $$"""
            You are the conductor Author. Read repository source at the current candidate to answer
            a residual specification clarification. You have read-only access. Return exactly one
            JSON object, optionally fenced as json. Allowed kinds:
            {"kind":"answer","text":"answer","evidenceReferences":["repo/relative/file:line"],"precedent":"optional"}
            {"kind":"ask-owner","question":"question","recommendation":"recommendation"}
            Ask the owner for decisions about agent authority, acceptance waivers, spend beyond budget,
            irreversible actions, external disclosure, or insufficient source evidence.

            Item: {{input.Item.Identity}}
            Question: {{input.Item.Question}}
            Goal brief:
            {{input.GoalBrief}}
            Refined spec:
            {{input.RefinedSpec}}
            Matching precedent:
            {{JsonSerializer.Serialize(input.MatchingPrecedent)}}
            """;
        WorkerProcessRunResult result;
        try
        {
            result = await WorkerProcessRunner.RunBufferedAsync(
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
