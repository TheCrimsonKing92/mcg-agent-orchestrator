using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

if (args.Length > 0 && args[0] == "--startup-launch") return StartupPipeProbe.Launch(args[1..]);
if (args.Length > 0 && args[0] == "--conhost-experiment") return await ConsoleHostExperimentProbe.Run(args[1..]);
if (args.Length > 0 && args[0] == "--startup-child") return StartupPipeProbe.Run(args[1..]);
if (args.Length > 0 && args[0] == "--priority-grandchild") return await BelowNormalGrandchildProbe.Run(args[1..]);

var options = ProbeOptions.Parse(args);

try
{
    var writers = new RetainedConsoleWriters(options.WriterInitialization == "early");

    // Prelude diagnostics must not initialize Console.Out/Error in the lazy-writer cases.
    WriteRawMarker(-11, "MARK:before");
    WriteRawMarker(-12, "EMARK:before");
    WriteHandleMarker("before", raw: true);

    for (var index = 0; index < options.Repeat; index++)
    {
        RunScope(options, index, writers);
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

static void RunScope(ProbeOptions options, int index, RetainedConsoleWriters writers)
{
    var scopes = new Stack<IDisposable>();
    try
    {
        for (var depth = 0; depth < options.Nest; depth++)
        {
            scopes.Push(options.Scope switch
            {
                "suppressed" => ProcessTreeGuiSuppression.AcquireSuppressedChildSpawn(),
                "console" => ProcessTreeGuiSuppression.AcquireConsoleForChildSpawn(),
                "start" => StartupPipeProbe.StartAndReturnScope(),
                _ => throw new ArgumentOutOfRangeException(nameof(options.Scope), options.Scope, "Unknown scope.")
            });
        }

        Console.WriteLine($"MARK:during:{index}");
        Console.Error.WriteLine($"EMARK:during:{index}");
        WriteHandleMarker($"during:{index}");
        if (options.UnrelatedThread)
        {
            // Keep the scope held until the unrelated thread has written through the
            // same retained writers the caller would keep across child launches.
            Task.Run(() =>
            {
                writers.Output.WriteLine($"MARK:unrelated:{index}");
                writers.Error.WriteLine($"EMARK:unrelated:{index}");
                writers.Output.Flush();
                writers.Error.Flush();
            }).GetAwaiter().GetResult();
        }
    }
    finally
    {
        while (scopes.TryPop(out var current)) current.Dispose();
    }
}

static void WriteRawMarker(int handleId, string marker)
{
    var bytes = Encoding.UTF8.GetBytes(marker + Environment.NewLine);
    if (!Native.WriteFile(Native.GetStdHandle(handleId), bytes, (uint)bytes.Length, out var written, IntPtr.Zero) || written != bytes.Length)
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to emit raw prelude marker.");
}

static void WriteHandleMarker(string phase, bool raw = false)
{
    var marker = $"HANDLE:{phase}:out={Native.GetFileType(Native.GetStdHandle(-11))},err={Native.GetFileType(Native.GetStdHandle(-12))},in={Native.GetFileType(Native.GetStdHandle(-10))},window={(Native.GetConsoleWindow() == IntPtr.Zero ? 0 : 1)}";
    if (raw) WriteRawMarker(-11, marker);
    else Console.WriteLine(marker);
}

sealed class ProbeOptions
{
    public required string Scope { get; init; }
    public required string WriterInitialization { get; init; }
    public required int Repeat { get; init; }
    public required int Nest { get; init; }
    public required bool UnrelatedThread { get; init; }

    public static ProbeOptions Parse(string[] args)
    {
        var values = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair.Length == 2 ? pair[1] : "true", StringComparer.Ordinal);
        return new ProbeOptions
        {
            Scope = values.GetValueOrDefault("--scope", "suppressed"),
            WriterInitialization = values.GetValueOrDefault("--writer-init", "early"),
            Repeat = int.Parse(values.GetValueOrDefault("--repeat", "1"), System.Globalization.CultureInfo.InvariantCulture),
            Nest = int.Parse(values.GetValueOrDefault("--nest", "1"), System.Globalization.CultureInfo.InvariantCulture),
            UnrelatedThread = values.ContainsKey("--unrelated-thread")
        };
    }
}

sealed class RetainedConsoleWriters
{
    private TextWriter? _output;
    private TextWriter? _error;

    public RetainedConsoleWriters(bool captureImmediately)
    {
        if (captureImmediately)
        {
            _output = Console.Out;
            _error = Console.Error;
        }
    }

    public TextWriter Output => _output ??= Console.Out;

    public TextWriter Error => _error ??= Console.Error;
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
    internal static extern bool WriteFile(IntPtr handle, byte[] bytes, uint count, out uint written, IntPtr overlapped);

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
