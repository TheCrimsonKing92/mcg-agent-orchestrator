namespace Mcg.AgentOrchestrator.App.Cli;

// One command's side effects, invoked only after its surrounding transaction returns.
internal sealed class PostCommitActions
{
    private readonly List<Action> actions = [];

    public void Register(Action action) => actions.Add(action);

    public void Run()
    {
        foreach (var action in actions)
            action();
    }
}
