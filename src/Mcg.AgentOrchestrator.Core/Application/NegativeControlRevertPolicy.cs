namespace Mcg.AgentOrchestrator.Core;

public static class NegativeControlRevertPolicy
{
    // Reviewed policy files read as data by tests, never build, run, or acceptance-harness inputs.
    public static readonly string[] RevertablePolicyFiles = [".gitattributes"];
}
