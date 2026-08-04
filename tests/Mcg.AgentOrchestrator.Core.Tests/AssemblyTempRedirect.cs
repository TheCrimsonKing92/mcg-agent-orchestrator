using System.Runtime.CompilerServices;

// Core.Tests builds a self-contained Microsoft.Testing.Platform executable. When that
// executable runs from a Low-integrity-labeled acceptance worktree, it cannot write to
// the Medium-integrity system temp directory returned by Path.GetTempPath().
//
// Redirect process-local temp resolution before any test creates scratch files. The
// canonical Low temp directory remains writable from both Low- and Medium-integrity
// runs; the output-local fallback keeps the test executable usable if that directory
// is unavailable.
internal static class AssemblyTempRedirect
{
    [ModuleInitializer]
    internal static void Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var candidate = SelectWritableRoot(EnumerateCandidateRoots(), TryPrepareRoot);
        if (candidate is null)
        {
            return;
        }

        Environment.SetEnvironmentVariable("TMP", candidate, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", candidate, EnvironmentVariableTarget.Process);
    }

    internal static string? SelectWritableRoot(
        IEnumerable<string> candidates,
        Func<string, bool> tryPrepareRoot)
    {
        foreach (var candidate in candidates)
        {
            if (tryPrepareRoot(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryPrepareRoot(string candidate)
    {
        var probePath = Path.Combine(candidate, $".write-probe-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(candidate);
            using (new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1,
                       FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A failed candidate is already unusable for test scratch files.
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots()
    {
        // The acceptance gate preserves the real LOCALAPPDATA variable while repointing
        // USERPROFILE to its hermetic profile. GetFolderPath expands the known-folder value
        // against that repointed profile, so consult the preserved variable first.
        foreach (var localAppData in new[]
                 {
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Temp", "Low", "mcg-tests");
            }
        }

        yield return Path.Combine(AppContext.BaseDirectory, ".test-tmp");
    }
}
