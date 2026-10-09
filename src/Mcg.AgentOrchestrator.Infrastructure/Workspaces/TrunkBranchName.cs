namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>The target repository's landing branch, independent of staging branches.</summary>
public static class TrunkBranchName
{
    public const string Default = "main";

    public static string Resolve(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? Default : Validate(configured.Trim());

    public static string Validate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Integration branch must be nonblank and contain no whitespace.", nameof(candidate));
        }

        return candidate;
    }
}
