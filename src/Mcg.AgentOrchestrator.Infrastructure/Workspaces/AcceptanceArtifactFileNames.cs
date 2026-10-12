using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceArtifactFileNames
{
    internal static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "acceptance-test-results" : sanitized;
    }

    internal static string Slug(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "check" : slug;
    }

    // A check names its own artifacts, and a focused-evidence check is named after its entire filter
    // expression, so the generated name grows with the request rather than staying bounded. Windows raises
    // ERROR_INVALID_NAME - "The filename, directory name, or volume label syntax is incorrect" - once a single
    // path component passes the 255-character NTFS limit, and a four-class evidence request produced a
    // 324-character TRX name. That killed the run inside MTP's TRX writer, where the IOException is unhandled:
    // the host exited 0xE0434352 and the gate booked the crash as a test failure. Bound every generated
    // component here rather than at the callers, and keep a stable hash of the full name so two long names
    // still land on two files.
    //
    // The 200 leaves headroom under 255 for suffixes callers append to a resolved path, the widest being the
    // ".{guid:N}.tmp" staging name used when copying receipts into an attempt folder.
    private const int MaxGeneratedFileNameLength = 200;

    internal static string BoundFileName(string stem, string suffix)
    {
        if (stem.Length + suffix.Length <= MaxGeneratedFileNameLength)
        {
            return stem + suffix;
        }

        var hash = GoalAcceptanceVerifier.ShortHash(stem);
        var keep = MaxGeneratedFileNameLength - suffix.Length - hash.Length - 1;
        return keep <= 0
            ? hash + suffix
            : $"{stem[..keep].TrimEnd('-', '.')}-{hash}{suffix}";
    }
}
