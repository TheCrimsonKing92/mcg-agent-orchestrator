using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

var options = ProbeOptions.Parse(args);

try
{
    if (options.WriterInitialization == "early")
    {
        _ = Console.Out;
        _ = Console.Error;
    }

    Console.WriteLine("MARK:before");
    Console.Error.WriteLine("EMARK:before");
    WriteHandleMarker("before");

    for (var index = 0; index < options.Repeat; index++)
    {
        if (options.OldMutation)
        {
            using var mutation = OldConsoleMutation.Create();
            Console.WriteLine($"MARK:during:{index}");
            Console.Error.WriteLine($"EMARK:during:{index}");
            WriteHandleMarker($"during:{index}");
        }
        else
        {
            RunScope(options.Scope, index, options.Nest);
        }
    }

    var input = Console.In.ReadLine();
    Console.WriteLine($"STDIN:{input}");
    Console.Error.WriteLine("EMARK:after");
    Console.WriteLine("MARK:after");
    WriteHandleMarker("after");
    Console.Out.Flush();
    Console.Error.Flush();
    return 0;
}
catch (Exception ex)
{
    try { Console.Error.WriteLine($"FATAL:{ex.GetType().Name}:{ex.Message}"); } catch { }
    return 1;
}

static void RunScope(string scope, int index, int nest)
{
    var scopes = new Stack<IDisposable>();
    try
    {
        for (var depth = 0; depth < nest; depth++)
        {
            scopes.Push(scope switch
            {
                "suppressed" => ProcessTreeGuiSuppression.AcquireSuppressedChildSpawn(),
                "console" => ProcessTreeGuiSuppression.AcquireConsoleForChildSpawn(),
                "start" => StartAndReturnScope(),
                _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope.")
            });
        }

        Console.WriteLine($"MARK:during:{index}");
        Console.Error.WriteLine($"EMARK:during:{index}");
        WriteHandleMarker($"during:{index}");
    }
    finally
    {
        while (scopes.TryPop(out var current)) current.Dispose();
    }
}

static IDisposable StartAndReturnScope()
{
    var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    var startInfo = new ProcessStartInfo(cmd, "/d /q /c exit 0")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    using var child = ProcessTreeGuiSuppression.Start(startInfo);
    child.WaitForExit(5_000);
    _ = child.StandardOutput.ReadToEnd();
    _ = child.StandardError.ReadToEnd();
    return NoopDisposable.Instance;
}

static void WriteHandleMarker(string phase)
{
    Console.WriteLine($"HANDLE:{phase}:out={Native.GetFileType(Native.GetStdHandle(-11))},err={Native.GetFileType(Native.GetStdHandle(-12))},in={Native.GetFileType(Native.GetStdHandle(-10))},window={(Native.GetConsoleWindow() == IntPtr.Zero ? 0 : 1)}");
}

sealed class ProbeOptions
{
    public required string Scope { get; init; }
    public required string WriterInitialization { get; init; }
    public required int Repeat { get; init; }
    public required int Nest { get; init; }
    public required bool OldMutation { get; init; }

    public static ProbeOptions Parse(string[] args)
    {
        var values = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair.Length == 2 ? pair[1] : "true", StringComparer.Ordinal);
        return new ProbeOptions
        {
            Scope = values.GetValueOrDefault("--scope", "suppressed"),
            WriterInitialization = values.GetValueOrDefault("--writer-init", "early"),
            Repeat = int.Parse(values.GetValueOrDefault("--repeat", "1"), System.Globalization.CultureInfo.InvariantCulture),
            Nest = int.Parse(values.GetValueOrDefault("--nest", "1"), System.Globalization.CultureInfo.InvariantCulture),
            OldMutation = values.ContainsKey("--old-mutation")
        };
    }
}

sealed class OldConsoleMutation : IDisposable
{
    private readonly Process _host;

    private OldConsoleMutation(Process host) => _host = host;

    public static OldConsoleMutation Create()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        _ = Native.FreeConsole();
        var startupInfo = new Native.StartupInfo
        {
            Cb = Marshal.SizeOf<Native.StartupInfo>(),
            DwFlags = Native.StartfUseShowWindow,
            WShowWindow = Native.SwHide
        };
        var commandLine = new StringBuilder($"\"{cmd}\" /d /q /k cd .");
        if (!Native.CreateProcessW(
                cmd,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                Native.CreateNewConsole | Native.CreateSuspended,
                IntPtr.Zero,
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                ref startupInfo,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed in RED control.");
        }

        try
        {
            if (Native.ResumeThread(processInformation.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed in RED control.");
            }

            for (var attempt = 0; !Native.AttachConsole(processInformation.ProcessId); attempt++)
            {
                if (Marshal.GetLastWin32Error() != 6 || attempt == 199)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "AttachConsole failed in RED control.");
                }

                Thread.Sleep(10);
            }

            return new OldConsoleMutation(Process.GetProcessById(processInformation.ProcessId));
        }
        catch
        {
            _ = Native.TerminateProcess(processInformation.Process, 0);
            throw;
        }
        finally
        {
            _ = Native.CloseHandle(processInformation.Process);
            _ = Native.CloseHandle(processInformation.Thread);
        }
    }

    public void Dispose()
    {
        _ = Native.FreeConsole();
        if (!_host.HasExited)
        {
            _host.Kill(entireProcessTree: true);
            _host.WaitForExit(5_000);
        }

        _host.Dispose();
    }
}

sealed class NoopDisposable : IDisposable
{
    public static readonly NoopDisposable Instance = new();
    public void Dispose() { }
}

static class Native
{
    internal const uint CreateSuspended = 0x00000004;
    internal const uint CreateNewConsole = 0x00000010;
    internal const uint StartfUseShowWindow = 0x00000001;
    internal const short SwHide = 0;

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    internal static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint DwFlags;
        public short WShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
