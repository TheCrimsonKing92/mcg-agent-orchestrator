using System.Runtime.CompilerServices;

// The Infrastructure.Tests project builds a self-contained Microsoft.Testing.Platform
// executable. When that exe is run directly (the MTP runner path used by the acceptance
// gate and by local validation), it inherits the repository tree's Low mandatory integrity
// label, so the process runs at Low integrity. A Low-integrity process cannot write to the
// Medium-integrity system temp directory that Path.GetTempPath() returns by default, which
// fails every test that builds a scratch workspace under the temp path.
//
// Redirect the process TMP/TEMP to a Low-integrity-writable location once, at module load,
// before any test (or child process it spawns) resolves Path.GetTempPath(). This is a no-op
// on non-Windows platforms, which have no integrity levels.
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
        // %LOCALAPPDATA%\Temp\Low is the canonical Windows Low-integrity temp area; it carries
        // a Low mandatory label so both Low- and Medium-integrity processes can write it, and
        // its path is short enough for the deeply nested workspaces these tests create.
        //
        // Read the LOCALAPPDATA VARIABLE before the known-folder API. GetFolderPath expands the
        // REG_EXPAND_SZ literal "%USERPROFILE%\AppData\Local" against THIS PROCESS'S environment
        // block, and the acceptance gate spawns us with USERPROFILE repointed at an empty hermetic
        // profile root. A freshly spawned child therefore resolves the known folder to
        // <profile-root>\AppData\Local - a directory that does not exist and is not writable - so
        // every workspace-building test failed with UnauthorizedAccessException on a path like
        // ...\mcg-hvp\AppData\Local\Temp\Low\mcg-tests. The gate sets the LOCALAPPDATA variable to
        // the REAL per-user location precisely so derived paths keep working; consult it first.
        // (Same trap, same fix, as WorkerShell.WindowsPowerShellCandidates.)
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

        // Fallback: a directory inside the build output, which lives in the Low-labeled repo
        // tree and is therefore always writable at the process integrity level.
        yield return Path.Combine(AppContext.BaseDirectory, ".test-tmp");
    }
}
