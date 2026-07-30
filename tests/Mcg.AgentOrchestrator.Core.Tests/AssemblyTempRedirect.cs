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

        foreach (var candidate in EnumerateCandidateRoots())
        {
            try
            {
                Directory.CreateDirectory(candidate);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            Environment.SetEnvironmentVariable("TMP", candidate, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TEMP", candidate, EnvironmentVariableTarget.Process);
            return;
        }
    }

    private static IEnumerable<string> EnumerateCandidateRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Temp", "Low", "mcg-tests");
        }

        yield return Path.Combine(AppContext.BaseDirectory, ".test-tmp");
    }
}
