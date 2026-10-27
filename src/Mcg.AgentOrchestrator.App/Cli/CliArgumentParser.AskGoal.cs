using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
    // A validated invocation; target identity is resolved again against the write snapshot.
    internal sealed record AskGoalArgs(string? ExplicitGoalSelector, string Question);

    internal static bool IsAskGoal(IReadOnlyList<string> parts) =>
        parts.Count > 0 && parts[0].Equals("ask-goal", StringComparison.OrdinalIgnoreCase);

    internal static AskGoalArgs ParseAskGoalArgs(IReadOnlyList<string> parts)
    {
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        try
        {
            var target = ParseGoalScopedTaskTargetArgs(parts);
            var question = string.Join(' ', target.Parts.Skip(target.ExplicitGoalSelector is null ? 1 : 3));
            if (string.IsNullOrWhiteSpace(question))
                throw new ArgumentException("Provide a question.");
            return new AskGoalArgs(target.ExplicitGoalSelector, question);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{ex.Message}{Environment.NewLine}{CliCommandHelp.AskGoalUsage}", ex);
        }
    }

    internal static Goal ResolveAskGoalTarget(
        AskGoalArgs args, AgentOrchestratorKernel kernel, Goal? currentGoal)
    {
        try
        {
            return args.ExplicitGoalSelector is null
                ? OrchestratorEntityResolver.RequireGoal(currentGoal)
                : OrchestratorEntityResolver.ResolveGoal(kernel, currentGoal, args.ExplicitGoalSelector);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new ArgumentException($"{ex.Message}{Environment.NewLine}{CliCommandHelp.AskGoalUsage}", ex);
        }
    }
}
