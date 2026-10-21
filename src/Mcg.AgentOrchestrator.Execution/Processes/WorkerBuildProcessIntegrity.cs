using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Only called with the native handle of our own still-suspended child, before candidate code runs.
internal static class WorkerBuildProcessIntegrity
{
    private const string LowIntegritySid = "S-1-16-4096";
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x00000020;
    private const uint LabelSecurityInformation = 0x00000010;
    private const int SeKernelObject = 6;
    private const uint AclRevision = 2;
    private const byte SystemMandatoryLabelAceType = 0x11;
    private const uint SystemMandatoryLabelNoWriteUp = 0x00000001;
    private const uint SystemMandatoryLabelNoReadUp = 0x00000002;
    private const uint ProcessLabelPolicy = SystemMandatoryLabelNoWriteUp | SystemMandatoryLabelNoReadUp;

    internal static bool IsLow(System.Diagnostics.Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, TokenQuery, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            var buffer = Marshal.AllocHGlobal(length);
            if (!ConvertStringSidToSidW(LowIntegritySid, out var sid)) { Marshal.FreeHGlobal(buffer); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return EqualSid(Marshal.PtrToStructure<MandatoryLabel>(buffer).Sid, sid);
            }
            finally { Marshal.FreeHGlobal(buffer); _ = LocalFree(sid); }
        }
    }

    internal static void Lower(SafeFileHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery | TokenAdjustDefault, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open owned suspended compiler token for integrity preparation.");
        using (token)
        {
            if (!ConvertStringSidToSidW(LowIntegritySid, out var sid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var label = new MandatoryLabel { Sid = sid, Attributes = SeGroupIntegrity };
                if (!SetTokenInformation(token, TokenIntegrityLevel, ref label, Marshal.SizeOf<MandatoryLabel>() + GetLengthSid(sid)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not lower owned compiler integrity before resume.");

                // A Low token with a Medium/NWNR process object cannot reopen itself for
                // ordinary runtime queries. Change only this owned child's mandatory label;
                // leave its DACL, parent, job and all other objects untouched.
                SetProcessLabel(process, sid);
                _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read back owned compiler LowIL token before resume.");
                    if (!EqualSid(Marshal.PtrToStructure<MandatoryLabel>(buffer).Sid, sid))
                        throw new UnauthorizedAccessException("Owned compiler LowIL token was not confirmed.");
                }
                finally { Marshal.FreeHGlobal(buffer); }
                RequireProcessLabel(process, sid);
            }
            finally { _ = LocalFree(sid); }
        }
    }

    private static void SetProcessLabel(SafeFileHandle process, IntPtr lowSid)
    {
        var aclLength = Marshal.SizeOf<AclHeader>() + Marshal.SizeOf<MandatoryLabelAceHeader>() + GetLengthSid(lowSid);
        var acl = Marshal.AllocHGlobal(aclLength);
        try
        {
            if (!InitializeAcl(acl, aclLength, AclRevision) ||
                !AddMandatoryAce(acl, AclRevision, 0, ProcessLabelPolicy, lowSid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not construct owned compiler Low/NWNR process label.");
            // LABEL_SECURITY_INFORMATION changes only the mandatory label, without SACL
            // privileges or changes to the owner, DACL or other SACL entries.
            var error = SetSecurityInfo(process, SeKernelObject, LabelSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, acl);
            if (error != 0)
                throw new Win32Exception(checked((int)error), "Could not set owned compiler Low/NWNR process label before resume.");
        }
        finally { Marshal.FreeHGlobal(acl); }
    }

    private static void RequireProcessLabel(SafeFileHandle process, IntPtr lowSid)
    {
        var error = GetSecurityInfo(process, SeKernelObject, LabelSecurityInformation,
            out _, out _, out _, out var acl, out var descriptor);
        try
        {
            if (error != 0)
                throw new Win32Exception(checked((int)error), "Could not read back owned compiler process label before resume.");
            if (acl == IntPtr.Zero || Marshal.PtrToStructure<AclHeader>(acl).AceCount != 1)
                throw new UnauthorizedAccessException("Owned compiler process label must contain exactly one mandatory label ACE.");
            if (!GetAce(acl, 0, out var ace))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read owned compiler mandatory label ACE before resume.");
            var header = Marshal.PtrToStructure<MandatoryLabelAceHeader>(ace);
            if (header.Type != SystemMandatoryLabelAceType || header.Flags != 0 ||
                header.Size != Marshal.SizeOf<MandatoryLabelAceHeader>() + GetLengthSid(lowSid) ||
                header.Mask != ProcessLabelPolicy ||
                !EqualSid(IntPtr.Add(ace, Marshal.SizeOf<MandatoryLabelAceHeader>()), lowSid))
                throw new UnauthorizedAccessException(
                    $"Owned compiler Low/NWNR process label was not confirmed (ACE type=0x{header.Type:X2}, flags=0x{header.Flags:X2}, mask=0x{header.Mask:X8}).");
        }
        finally { if (descriptor != IntPtr.Zero) _ = LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MandatoryLabel { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct AclHeader { public byte Revision; public byte Reserved; public ushort Size; public ushort AceCount; public ushort Reserved2; }
    [StructLayout(LayoutKind.Sequential)] private struct MandatoryLabelAceHeader { public byte Type; public byte Flags; public ushort Size; public uint Mask; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeHandle process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSidToSidW(string text, out IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(SafeFileHandle token, int kind, ref MandatoryLabel label, int length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeFileHandle token, int kind, IntPtr buffer, int length, out int needed);
    [DllImport("advapi32.dll")] private static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern bool EqualSid(IntPtr left, IntPtr right);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool InitializeAcl(IntPtr acl, int length, uint revision);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AddMandatoryAce(IntPtr acl, uint revision, uint flags, uint policy, IntPtr sid);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeHandle handle, int objectType, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeHandle handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
