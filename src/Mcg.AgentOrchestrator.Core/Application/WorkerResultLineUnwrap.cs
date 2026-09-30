namespace Mcg.AgentOrchestrator.Core;

public static class WorkerResultLineUnwrap
{
    public static string Unwrap(string trimmedLine)
    {
        if (trimmedLine.Length >= 3 &&
            trimmedLine[0] == '`' &&
            trimmedLine[^1] == '`' &&
            !trimmedLine.AsSpan(1, trimmedLine.Length - 2).Contains('`'))
        {
            return trimmedLine[1..^1].Trim();
        }

        return trimmedLine;
    }
}
