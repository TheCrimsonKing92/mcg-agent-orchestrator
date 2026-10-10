namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerQuestionViewOnly
{
    internal static bool IsViewOnly(OwnerQuestionKind kind) =>
        kind is OwnerQuestionKind.StewardHold or OwnerQuestionKind.ExperimentReading;

    internal static string Notice(OwnerQuestionKind kind, string goalId) => kind switch
    {
        OwnerQuestionKind.StewardHold => $"Steward questions are answered through goal verbs for now; use the CLI retry/adjudicate commands for goal {goalId}.",
        OwnerQuestionKind.ExperimentReading => "Decide this experiment on the command line: run experiment-show to print the reading, then experiment-decide with the outcome, evidence and action.",
        _ => throw new InvalidOperationException("This question is not view only.")
    };
}
