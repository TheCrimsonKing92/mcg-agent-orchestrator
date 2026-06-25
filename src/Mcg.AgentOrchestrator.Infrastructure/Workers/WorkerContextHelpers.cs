namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerContextHelpers
{
    public static string TrimArtifactBlock(string value, int maxChars)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars] +
            Environment.NewLine +
            $"...[truncated {trimmed.Length - maxChars} chars for context artifact budget]...";
    }
}