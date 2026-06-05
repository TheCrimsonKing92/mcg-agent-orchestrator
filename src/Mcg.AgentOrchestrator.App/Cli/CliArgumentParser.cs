using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
public static void RequirePartCount(IReadOnlyList<string> parts, int expected, string usage)
{
    if (parts.Count < expected)
    {
        throw new ArgumentException($"Usage: {usage}");
    }
}
}


