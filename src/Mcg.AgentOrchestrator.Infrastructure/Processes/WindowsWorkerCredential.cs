using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Reads the dedicated worker account's password back from Windows Credential Manager so the
/// orchestrator can launch the worker AS that low-privilege account (the OS worker sandbox).
/// The credential is stored once by scripts/Setup-WorkerSandbox.ps1; the orchestrator runs as the
/// operator and reads it via CredRead (validated working non-elevated by Test-WorkerSandbox.ps1).
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsWorkerCredential
{
    private const int CredTypeGeneric = 1;

    /// <summary>
    /// Returns the stored password for <paramref name="credentialTarget"/>, or null if no such
    /// generic credential is readable by the current user.
    /// </summary>
    public static string? TryReadPassword(string credentialTarget)
    {
        if (!CredReadW(credentialTarget, CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            // The blob is the password as UTF-16 bytes (no terminator); size is in bytes.
            return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
