namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class AuthorDraftRepositoryNotAtMainException(
    string condition, string head, string main, string toplevel, string configuredRoot)
    : InvalidOperationException(
        "Author drafting requires the repository root checked out at current main HEAD. " +
        $"Failed condition: {condition}; HEAD={head}; main={main}; " +
        $"toplevel={toplevel}; configured root={configuredRoot}.")
{
    internal string Condition { get; } = condition;
    internal string Head { get; } = head;
    internal string Main { get; } = main;
    internal string Toplevel { get; } = toplevel;
    internal string ConfiguredRoot { get; } = configuredRoot;
}
