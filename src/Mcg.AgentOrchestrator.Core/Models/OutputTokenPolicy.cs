namespace Mcg.AgentOrchestrator.Core;

// Single source of truth for output token caps. Consumers (AgentTaskRunner,
// AgentCatalog, providers, tests) must reference these constants instead of
// declaring their own cap literals.
public static class OutputTokenPolicy
{
    public const int RoutinePaidMaxOutputTokens = 2048;
    public const int ComplexPaidMaxOutputTokens = 4096;
    public const int RoutineLocalMaxOutputTokens = 2048;
    public const int ComplexLocalMaxOutputTokens = 8192;
}
