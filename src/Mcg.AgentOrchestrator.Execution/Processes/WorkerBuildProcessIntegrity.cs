using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Only called with the native handle of our own still-suspended child, before candidate code runs.
internal static class WorkerBuildProcessIntegrity
{
    internal static bool IsLow(System.Diagnostics.Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, 0x8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            _ = GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
            var buffer = Marshal.AllocHGlobal(length);
            if (!ConvertStringSidToSidW("S-1-16-4096", out var sid)) { Marshal.FreeHGlobal(buffer); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                if (!GetTokenInformation(token, 25, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return EqualSid(Marshal.PtrToStructure<MandatoryLabel>(buffer).Sid, sid);
            }
            finally { Marshal.FreeHGlobal(buffer); _ = LocalFree(sid); }
        }
    }

    internal static void Lower(SafeFileHandle process)
    {
        if (!OpenProcessToken(process, 0x88, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!ConvertStringSidToSidW("S-1-16-4096", out var sid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var label = new MandatoryLabel { Sid = sid, Attributes = 0x20 };
                if (!SetTokenInformation(token, 25, ref label, Marshal.SizeOf<MandatoryLabel>() + GetLengthSid(sid)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not lower owned compiler integrity before resume.");
                _ = GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, 25, buffer, length, out _) ||
                        !EqualSid(Marshal.PtrToStructure<MandatoryLabel>(buffer).Sid, sid))
                        throw new UnauthorizedAccessException("Owned compiler LowIL token was not confirmed.");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { _ = LocalFree(sid); }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MandatoryLabel { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeHandle process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSidW(string text, out IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(SafeFileHandle token, int kind, ref MandatoryLabel label, int length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeFileHandle token, int kind, IntPtr buffer, int length, out int needed);
    [DllImport("advapi32.dll")] private static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern bool EqualSid(IntPtr left, IntPtr right);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
