namespace Mcg.AgentOrchestrator.Core;

public static class VerificationSnapshotText
{
    public const string StoredAsBoundedText = "stored-as-bounded-text";

    public static (string? Text, string? UnavailableReason) Compact(
        string? authoritativeText, string? unavailableReason, string boundedText)
    {
        return authoritativeText is not null
            && unavailableReason is null
            && string.Equals(authoritativeText, boundedText, StringComparison.Ordinal)
                ? (null, StoredAsBoundedText)
                : (authoritativeText, unavailableReason);
    }

    public static (string? Text, string? UnavailableReason) Expand(
        string? storedText, string? storedReason, string boundedText)
    {
        if (storedText is null && string.Equals(storedReason, StoredAsBoundedText, StringComparison.Ordinal))
        {
            return (boundedText, null);
        }

        return string.Equals(storedText, boundedText, StringComparison.Ordinal)
            ? (boundedText, storedReason)
            : (storedText, storedReason);
    }
}
