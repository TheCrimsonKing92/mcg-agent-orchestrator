using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Grants the dedicated low-privilege worker account write access to exactly the per-run writable
/// set (the worktree and the repo's common .git, which the worktree commits into), and nothing
/// else. The account is a separate standard user, so everywhere it is NOT granted is denied by
/// default — that is the OS-enforced confinement. The operator owns these directories, so granting
/// needs no elevation. The worker's own profile (TEMP, NuGet, dotnet caches) is writable to it by
/// default and needs no grant.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WorkerSandboxAcl
{
    /// <summary>
    /// Grants <paramref name="account"/> Modify (+ inheritance) on <paramref name="directory"/>.
    /// Idempotent: adding the same allow rule again is a no-op union. Returns false if the directory
    /// does not exist.
    /// </summary>
    public static bool GrantModify(string directory, string account)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(
            new NTAccount(account),
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return true;
    }

    /// <summary>
    /// Removes a previously-granted Modify allow rule for <paramref name="account"/> on
    /// <paramref name="directory"/> (best-effort cleanup after a dispatch). Never throws.
    /// </summary>
    public static void RevokeModify(string directory, string account)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            var rule = new FileSystemAccessRule(
                new NTAccount(account),
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
        catch
        {
            // Cleanup is best-effort; a leftover allow rule on a transient worktree is harmless.
        }
    }
}
