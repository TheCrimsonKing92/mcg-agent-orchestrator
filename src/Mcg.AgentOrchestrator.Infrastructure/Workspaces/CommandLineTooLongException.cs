using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class CommandLineTooLongException(int length, int limit, string firstArgument)
    : Exception(string.Create(CultureInfo.InvariantCulture,
        $"Windows cmd.exe command line is {length} characters, which exceeds the cmd.exe limit of {limit} characters; first argument: {firstArgument}"))
{
    public int Length { get; } = length;
    public int Limit { get; } = limit;
    public string FirstArgument { get; } = firstArgument;
}
