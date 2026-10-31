using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WindowsHandleListProcessLauncher
{
    // This boundary inherits only the designated logs; no ambient standard handles.
    internal static DetachedProcessCreationOutcome Launch(
        string application, string commandLine, string environment, string workingDirectory,
        uint creationFlags, IntPtr stdoutHandle, IntPtr stderrHandle)
    {
        using var attributeList = CreateInheritedHandleList(stdoutHandle, stderrHandle);
        var startupInfo = new WindowsProcessStartupInfoEx
        {
            cb = Marshal.SizeOf<WindowsProcessStartupInfoEx>(),
            dwFlags = (int)WindowsStartupInfoFlags.UseStdHandles,
            hStdOutput = stdoutHandle,
            hStdError = stderrHandle,
            lpAttributeList = attributeList.AttributeList
        };
        // CreateProcessW may mutate this buffer, so each attempt starts from the original.
        var created = CreateProcessW(
            lpApplicationName: application,
            lpCommandLine: new StringBuilder(commandLine),
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: true,
            dwCreationFlags: (WindowsCreationFlags)creationFlags,
            lpEnvironment: environment,
            lpCurrentDirectory: workingDirectory,
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out var information);
        var nativeError = created ? 0 : Marshal.GetLastWin32Error();
        return new DetachedProcessCreationOutcome(created, information.hProcess,
            information.hThread, (int)information.dwProcessId, nativeError);
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key);
            builder.Append('=');
            builder.Append(pair.Value);
            builder.Append('\0');
        }
        builder.Append('\0');
        return builder.ToString();
    }

    private static WindowsInheritedHandleList CreateInheritedHandleList(params IntPtr[] handles)
    {
        var size = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to size Windows process attribute list.");

        var attributeList = Marshal.AllocHGlobal(size);
        var handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize Windows process attribute list.");

            initialized = true;
            Marshal.Copy(handles, 0, handleList, handles.Length);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    new IntPtr(ProcThreadAttributeHandleList),
                    handleList,
                    new IntPtr(IntPtr.Size * handles.Length),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to restrict inherited Windows process handles.");
            }

            return new WindowsInheritedHandleList(attributeList, handleList, initialized);
        }
        catch
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    internal static IntPtr CreateInheritedOutputFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var securityAttributes = new WindowsSecurityAttributes
        {
            nLength = Marshal.SizeOf<WindowsSecurityAttributes>(),
            bInheritHandle = true
        };

        var handle = CreateFileW(
            path,
            WindowsFileAccess.GenericWrite,
            WindowsFileShare.Read | WindowsFileShare.Delete,
            ref securityAttributes,
            WindowsCreationDisposition.CreateAlways,
            WindowsFileFlags.FileAttributeNormal,
            IntPtr.Zero);
        if (handle == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open conduct loop successor log file: {path}");

        return handle;
    }

    [Flags]
    internal enum WindowsCreationFlags : uint
    {
        CreateNewProcessGroup = 0x00000200,
        CreateUnicodeEnvironment = 0x00000400,
        CreateBreakawayFromJob = 0x01000000,
        CreateNoWindow = 0x08000000,
        ExtendedStartupInfoPresent = 0x00080000
    }

    private const int ProcThreadAttributeHandleList = 0x00020002;

    [Flags]
    private enum WindowsStartupInfoFlags : int
    {
        UseStdHandles = 0x00000100
    }

    [Flags]
    private enum WindowsFileAccess : uint
    {
        GenericWrite = 0x40000000
    }

    [Flags]
    private enum WindowsFileShare : uint
    {
        Read = 0x00000001,
        Delete = 0x00000004
    }

    private enum WindowsCreationDisposition : uint
    {
        CreateAlways = 2
    }

    [Flags]
    private enum WindowsFileFlags : uint
    {
        FileAttributeNormal = 0x00000080
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsSecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsProcessStartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsProcessStartupInfoEx
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
        public IntPtr lpAttributeList;
    }

    private sealed class WindowsInheritedHandleList : IDisposable
    {
        public WindowsInheritedHandleList(IntPtr attributeList, IntPtr handleList, bool initialized)
        {
            AttributeList = attributeList;
            HandleList = handleList;
            Initialized = initialized;
        }

        public IntPtr AttributeList { get; }
        private IntPtr HandleList { get; }
        private bool Initialized { get; }

        public void Dispose()
        {
            if (Initialized)
                DeleteProcThreadAttributeList(AttributeList);
            Marshal.FreeHGlobal(HandleList);
            Marshal.FreeHGlobal(AttributeList);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        WindowsCreationFlags dwCreationFlags,
        string? lpEnvironment,
        string? lpCurrentDirectory,
        ref WindowsProcessStartupInfoEx lpStartupInfo,
        out WindowsProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        WindowsFileAccess dwDesiredAccess,
        WindowsFileShare dwShareMode,
        ref WindowsSecurityAttributes lpSecurityAttributes,
        WindowsCreationDisposition dwCreationDisposition,
        WindowsFileFlags dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
