using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class SystemConsoleInput : IOwnerConsoleInput
{
    private readonly OwnerConsoleLineEditor _editor;

    public SystemConsoleInput() : this(OwnerConsoleLineEditor.System) { }
    internal SystemConsoleInput(OwnerConsoleLineEditor editor) => _editor = editor;
    public bool IsEditingLine => _editor.IsEditingLine;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        _editor.IsInputRedirected
            ? new(Task.Run(() => System.Console.ReadLine(), cancellationToken))
            : _editor.ReadLineAsync(cancellationToken);
}

internal sealed class SystemConsoleOutput : IOwnerConsoleOutput
{
    private readonly OwnerConsoleLineEditor _editor;

    public SystemConsoleOutput() : this(OwnerConsoleLineEditor.System) { }
    internal SystemConsoleOutput(OwnerConsoleLineEditor editor) => _editor = editor;
    public void Write(string text) => _editor.WriteOutput(text, newLine: false);
    public void WriteLine(string text) => _editor.WriteOutput(text, newLine: true);
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
