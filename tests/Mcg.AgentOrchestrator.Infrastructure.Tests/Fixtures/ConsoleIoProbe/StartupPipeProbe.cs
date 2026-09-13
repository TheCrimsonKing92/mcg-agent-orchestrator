using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// This extra process is intentional: Process.Start with redirected streams supplies
// STARTF_USESTDHANDLES, which prevents the console-attachment regression being tested.
internal static class StartupPipeProbe
{
    internal static int Launch(string[] args)
    {
        var duplicates = new List<IntPtr>();
        IntPtr attributes = IntPtr.Zero, handleList = IntPtr.Zero;
        var attributesInitialized = false;
        var child = default(Native.ProcessInformation);
        using var group = OwnedProcessGroup.CreateContained();
        try
        {
            foreach (var id in new[] { -10, -11, -12 })
            {
                if (!DuplicateHandle(GetCurrentProcess(), Native.GetStdHandle(id), GetCurrentProcess(), out var duplicate, 0, true, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                duplicates.Add(duplicate);
            }
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(IntPtr.Size * duplicates.Count);
            Marshal.Copy(duplicates.ToArray(), 0, handleList, duplicates.Count);
            if (!UpdateProcThreadAttribute(attributes, 0, (nuint)0x20002, handleList, (nuint)(IntPtr.Size * duplicates.Count), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("Host path unavailable");
            if (!Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Launch this fixture using dotnet and its managed DLL.");
            var arguments = new[] { dotnet, Assembly.GetExecutingAssembly().Location, "--startup-child" }
                .Concat(args).Concat(duplicates.Select(h => h.ToInt64().ToString(CultureInfo.InvariantCulture)));
            var startup = new StartupInfoEx
            {
                Startup = new Native.StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>(), DwFlags = 1, WShowWindow = 0 },
                Attributes = attributes
            };
            if (!CreateProcessW(dotnet, new StringBuilder(string.Join(" ", arguments.Select(Quote))), IntPtr.Zero, IntPtr.Zero,
                    true, 0x08000000 | 0x00080000 | 0x00000004, IntPtr.Zero, null, ref startup, out child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var process = Process.GetProcessById(child.ProcessId);
            group.Add(process); // Contain the suspended child and its legacy console host before it can run.
            if (Native.ResumeThread(child.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WaitForSingleObject(child.Process, 20_000) != 0) throw new TimeoutException("Startup pipe probe exceeded 20 seconds.");
            if (!GetExitCodeProcess(child.Process, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return checked((int)code);
        }
        finally
        {
            group.Kill();
            if (child.Process != IntPtr.Zero) { _ = Native.TerminateProcess(child.Process, 1); _ = Native.CloseHandle(child.Process); }
            if (child.Thread != IntPtr.Zero) _ = Native.CloseHandle(child.Thread);
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            foreach (var handle in duplicates) _ = Native.CloseHandle(handle);
        }
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 7) throw new ArgumentException("report legacy|current early|lazy scope stdin stdout stderr");
        var samples = new List<object>();
        var errors = new List<string>();
        uint flags = 0;
        string? failure = null, input = null;
        try
        {
            for (var index = 0; index < 3; index++)
                if (!SetStdHandle(-10 - index, new IntPtr(long.Parse(args[4 + index], CultureInfo.InvariantCulture))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            GetStartupInfoW(out var startup);
            flags = startup.DwFlags;
            if (flags != 1 || Enumerable.Range(0, 3).Any(i => Native.GetFileType(Native.GetStdHandle(-10 - i)) != 3))
                throw new InvalidOperationException($"Unqualified startup: flags={flags}; expected SHOWWINDOW only and three pipes.");
            var writers = new RetainedConsoleWriters(args[2] == "early");
            for (var iteration = 0; iteration < 2; iteration++)
            {
                Sample($"before:{iteration}");
                using (args[1] == "legacy" ? OldConsoleMutation.Create() : Acquire(args[3]))
                {
                    Sample($"inside:{iteration}");
                    Write(writers.Output, $"OUT:inside:{iteration}");
                    Write(writers.Error, $"ERR:inside:{iteration}");
                }
                Sample($"after:{iteration}");
                Write(writers.Output, $"OUT:after:{iteration}");
                Write(writers.Error, $"ERR:after:{iteration}");
            }
            if (Native.GetFileType(Native.GetStdHandle(-10)) == 3) input = Console.In.ReadLine();
        }
        catch (Exception exception) { failure = exception.ToString(); }
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { flags, samples, errors, input, failure }));
        return failure is null ? 0 : 1;

        void Sample(string phase) => samples.Add(new
        {
            phase,
            handles = Enumerable.Range(0, 3).Select(i => Native.GetStdHandle(-10 - i).ToInt64()).ToArray(),
            types = Enumerable.Range(0, 3).Select(i => Native.GetFileType(Native.GetStdHandle(-10 - i))).ToArray()
        });
        void Write(TextWriter writer, string marker)
        {
            try { writer.WriteLine(marker); writer.Flush(); }
            catch (Exception exception) { errors.Add($"{marker}: {exception.GetType().Name}: {exception.Message}"); }
        }
    }

    private static IDisposable Acquire(string scope) => scope switch
    {
        "suppressed" => ProcessTreeGuiSuppression.AcquireSuppressedChildSpawn(),
        "console" => ProcessTreeGuiSuppression.AcquireConsoleForChildSpawn(),
        "start" => StartAndReturnScope(),
        _ => throw new ArgumentException("Unknown scope")
    };

    internal static IDisposable StartAndReturnScope()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var startInfo = new ProcessStartInfo(cmd, "/d /q /c exit 0")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var child = ProcessTreeGuiSuppression.Start(startInfo);
        if (!child.WaitForExit(5_000))
        {
            child.Kill(entireProcessTree: true);
            _ = child.WaitForExit(5_000);
            throw new TimeoutException("Scoped child launch exceeded five seconds.");
        }
        _ = child.StandardOutput.ReadToEnd();
        _ = child.StandardError.ReadToEnd();
        if (child.ExitCode != 0) throw new InvalidOperationException($"Scoped child failed: {child.ExitCode}");
        return NoopDisposable.Instance;
    }

    private static string Quote(string value)
    {
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            output.Append('\\', slashes * (character == '"' ? 2 : 1));
            if (character == '"') output.Append('\\');
            output.Append(character);
            slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public Native.StartupInfo Startup; public IntPtr Attributes; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out Native.ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr process, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetStdHandle(int id, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern void GetStartupInfoW(out Native.StartupInfo startup);
}
