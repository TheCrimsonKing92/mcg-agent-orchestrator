using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class SystemConsoleInput : IOwnerConsoleInput
{
    private int _editing;
    public bool IsEditingLine => Volatile.Read(ref _editing) != 0;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        new(Task.Run(() => System.Console.IsInputRedirected
            ? System.Console.ReadLine()
            : ReadInteractiveLine(), cancellationToken));

    private string ReadInteractiveLine()
    {
        var line = new System.Text.StringBuilder();
        while (true)
        {
            var key = System.Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                System.Console.WriteLine();
                Volatile.Write(ref _editing, 0);
                return line.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (line.Length > 0)
                {
                    line.Length--;
                    System.Console.Write("\b \b");
                }
                Volatile.Write(ref _editing, line.Length == 0 ? 0 : 1);
                continue;
            }
            if (char.IsControl(key.KeyChar)) continue;
            Volatile.Write(ref _editing, 1);
            line.Append(key.KeyChar);
            System.Console.Write(key.KeyChar);
        }
    }
}

internal sealed class SystemConsoleOutput : IOwnerConsoleOutput
{
    public void Write(string text) { System.Console.Write(text); System.Console.Out.Flush(); }
    public void WriteLine(string text) { System.Console.WriteLine(text); System.Console.Out.Flush(); }
}

internal sealed class OwnerDigestSummaryAdapter(OrchestratorWorkspace workspace) : IOwnerDigestSummary
{
    public IReadOnlyList<string> ReadSummaryLines()
    {
        if (!File.Exists(workspace.SqliteStatePath)) return ["Owner digest: state unavailable"];
        try
        {
            var report = CliOwnerDigestCommand.Read(workspace, new SystemClock());
            return
            [
                $"Owner digest: landed={report.Totals.LandedGoals} pending={report.Totals.Pending} reverts={report.Reverts}",
                $"Interventions: {report.Totals.Interventions.Total}; escapes={report.Totals.Escapes}"
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return ["Owner digest: unavailable"]; }
    }
}

internal sealed class GoalEventFileTail(string directory) : IGoalEventTail
{
    public IReadOnlyList<string> ReadLast(string goalId, int count)
    {
        var path = Path.Combine(directory, $"{goalId}.jsonl");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>();
        while (reader.ReadLine() is { } line)
        { if (tail.Count == count) tail.Dequeue(); tail.Enqueue(line); }
        return tail.ToArray();
    }
}

internal sealed class AttentionAnswerHandlerAdapter(OrchestratorWorkspace workspace) : IOwnerAnswerSubmitter
{
    public void Submit(OwnerQuestion question, string answer)
    {
        if (question.Kind == OwnerQuestionKind.StewardHold)
            throw new InvalidOperationException("Steward holds have no attention answer handler.");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
        var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
        Goal? currentGoal = null;
        CliPersistentStateRunner.ExecuteCommand(
            ["attention", "answer", question.ItemId, answer],
            repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
            NullOperatorChannel.Instance);
    }
}
