namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliIdentifierText
{
    internal static string ShortId(string value) => value[..Math.Min(8, value.Length)];
}
