using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ProcessSpawnGuard
{
    private const uint HandleFlagInherit = 0x00000001;
    private const uint FileTypeDisk = 0x0001;
    private const uint FileNameNormalized = 0x0;
    private const int SystemExtendedHandleInformation = 64;
    private const int InitialHandleBufferSize = 0x10000;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    public static int ClearInheritableStateDatabaseHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        return ClearInheritableFileHandles("state.db", "state.db-wal", "state.db-shm");
    }

    internal static int ClearInheritableFileHandles(params string[] fileNames)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var cleared = 0;
        foreach (var handle in EnumerateCurrentProcessHandles())
        {
            if (!GetHandleInformation(handle, out var flags) ||
                (flags & HandleFlagInherit) == 0)
            {
                continue;
            }

            var hasResolvedTargetPath = TryGetDiskHandlePath(handle, out var path) && fileNames.Length > 0;
            if (hasResolvedTargetPath &&
                !fileNames.Any(fileName => path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!SetHandleInformation(handle, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to clear inheritable flag for disk handle.");
            }

            cleared++;
        }

        return cleared;
    }

    internal static bool IsHandleInheritable(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!GetHandleInformation(handle, out var flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to inspect handle flags.");
        }

        return (flags & HandleFlagInherit) != 0;
    }

    private static IEnumerable<IntPtr> EnumerateCurrentProcessHandles()
    {
        var bufferLength = InitialHandleBufferSize;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(bufferLength);
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferLength, out var requiredLength);
                if (status == 0)
                {
                    break;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != StatusInfoLengthMismatch &&
                    status != StatusBufferTooSmall &&
                    status != StatusBufferOverflow)
                {
                    yield break;
                }

                bufferLength = Math.Max(requiredLength, bufferLength * 2);
            }

            var handleCount = Marshal.ReadIntPtr(buffer).ToInt64();
            var entryPointer = IntPtr.Add(buffer, IntPtr.Size * 2);
            var entrySize = Marshal.SizeOf<SystemHandleTableEntryInfoEx>();
            var currentProcessId = Environment.ProcessId;
            for (long index = 0; index < handleCount; index++)
            {
                var entry = Marshal.PtrToStructure<SystemHandleTableEntryInfoEx>(IntPtr.Add(entryPointer, checked((int)(index * entrySize))));
                if (entry.UniqueProcessId.ToInt64() == currentProcessId)
                {
                    yield return entry.HandleValue;
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static bool TryGetDiskHandlePath(IntPtr handle, out string path)
    {
        path = string.Empty;
        if (GetFileType(handle) != FileTypeDisk)
        {
            return false;
        }

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, FileNameNormalized);
        if (length == 0)
        {
            return false;
        }

        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, FileNameNormalized);
            if (length == 0)
            {
                return false;
            }
        }

        path = buffer.ToString();
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleTableEntryInfoEx
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, int cchFilePath, uint dwFlags);
}
