using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    internal const string HermeticGateGitConfig =
        "[core]\n\tautocrlf = true\n\tsymlinks = false\n\tfscache = true\n[init]\n\tdefaultBranch = master\n";

    private static void ConfigureHermeticGitConfiguration(
        IDictionary<string, string?> environment,
        string profileRoot)
    {
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "never";
        environment["GIT_CONFIG_GLOBAL"] = EnsureHermeticGateGitConfig(profileRoot);
    }

    internal static string EnsureHermeticGateGitConfig(string profileRoot)
    {
        var directory = Path.Combine(profileRoot, "git");
        var destination = Path.Combine(directory, "gate-gitconfig");
        var expected = new UTF8Encoding(false).GetBytes(HermeticGateGitConfig);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(destination).ToUpperInvariant())));
        using var mutex = new Mutex(false, "mcg-gate-gitconfig-" + identity);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
            {
                throw new IOException($"Timed out waiting to write the hermetic git config at '{destination}'.");
            }
        }
        catch (AbandonedMutexException)
        {
            // The prior owner died while writing; inspect the file under the acquired mutex.
        }

        try
        {
            Directory.CreateDirectory(directory);
            // Git writes global config through this sibling lockfile. Keep its name occupied
            // so a gate cannot replace the shared config even when Git clears ReadOnly.
            Directory.CreateDirectory(destination + ".lock");
            if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(expected))
            {
                File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
                return destination;
            }

            var temporary = Path.Combine(directory, "gate-gitconfig." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, expected);
                try
                {
                    if (File.Exists(destination))
                    {
                        File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                    }

                    File.Move(temporary, destination, overwrite: true);
                }
                catch (IOException) when (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(expected))
                {
                    // A concurrent writer may have installed the same content first.
                }
                catch (UnauthorizedAccessException) when (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(expected))
                {
                    // A concurrent writer may have installed the same read-only content first.
                }
                File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return destination;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
