using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ProjectOwnerAnswerOutcome(bool Success, string Message,
    OwnerAnswer? Answer = null, string ErrorPrefix = "Refused: ");
