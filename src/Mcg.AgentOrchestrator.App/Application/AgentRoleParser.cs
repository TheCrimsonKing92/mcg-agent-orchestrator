using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Application;

internal static class AgentRoleParser
{
    public static AgentRole Parse(string value)
    {
        return Enum.TryParse<AgentRole>(value, ignoreCase: true, out var role)
            ? role
            : throw new ArgumentException($"Role must be one of: {string.Join(", ", Enum.GetNames<AgentRole>())}.");
    }
}
