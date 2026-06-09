namespace Mcg.AgentOrchestrator.Core;

internal static class LocalModelPromptOptimizer
{
    private static readonly HashSet<string> LocalProviders =
        new(StringComparer.OrdinalIgnoreCase) { "Ollama" };

    public static bool IsLocalProvider(string providerName) =>
        LocalProviders.Contains(providerName);

    public static string OptimizeSystemPrompt(string basePrompt, AgentRole role)
    {
        var roleDirective = role switch
        {
            AgentRole.Planner =>
                "You are a Planner. Produce a concrete implementation plan with files, tasks, dependencies, and verification criteria.",
            AgentRole.Researcher =>
                "You are a Researcher. Find and report concrete evidence from the codebase: file paths, symbols, commands, and constraints.",
            AgentRole.Developer =>
                "You are a Developer. Implement the requested change. Report changed files and verification steps.",
            AgentRole.Tester =>
                "You are a Tester. Write executable test cases with full implementations. Cover happy path, edge cases, and failure modes.",
            AgentRole.Reviewer =>
                "You are a Reviewer. Review code for correctness, security, and quality. Order findings by severity with file paths and corrected code.",
            _ => basePrompt
        };

        var outputTemplate = role switch
        {
            AgentRole.Tester =>
                " Respond with: ## Test Code (complete, executable [Fact] methods — this is the primary deliverable)," +
                " ## Coverage Summary (which scenarios are covered), ## Blockers.",
            AgentRole.Developer =>
                " Respond with: ## Implementation (complete source code — this is the primary deliverable)," +
                " ## Changed Files, ## Verification Steps, ## Blockers.",
            _ =>
                " Respond with: ## Results, ## Evidence, ## Blockers, ## Human Input Needed (if any)."
        };

        return roleDirective + outputTemplate +
            " If you need operator input, include a line starting with HUMAN_INPUT: followed by the question." +
            " Be direct and concrete. Do not summarize generically.";
    }

    public static string AppendThinkingGuidance(string userPrompt, AgentRole role)
    {
        var guidance = role switch
        {
            AgentRole.Developer =>
                "\n\n/no_think",
            _ =>
                "\n\nKeep internal reasoning brief. Focus output tokens on content, not deliberation."
        };

        return userPrompt + guidance;
    }
}
