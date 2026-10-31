using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorStartCallerProbe
{
    internal static int Run(string root, string token)
    {
        // Own a console even when the test host has none. Preserve the merged output
        // pipe because AllocConsole may initialize the process standard handles.
        var streams = new[] { -10, -11, -12 };
        var handles = streams.Select(GetStdHandle).ToArray();
        if (GetConsoleProcessList(new uint[1], 1) == 0)
        {
            if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (var index = 0; index < streams.Length; index++)
                if (!SetStdHandle(streams[index], handles[index]))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var consoleProcesses = new uint[64];
        var consoleCount = GetConsoleProcessList(consoleProcesses, (uint)consoleProcesses.Length);
        if (consoleCount == 0 || consoleCount > consoleProcesses.Length)
            throw new InvalidOperationException("Caller must have a test-owned console.");
        File.WriteAllText(Path.Combine(root, "caller-console.json"), JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            processIds = consoleProcesses.Take((int)consoleCount)
        }));
        // Prove the fixture actually supplies inheritable caller pipe handles.
        foreach (var stream in new[] { -11, -12 })
        {
            var handle = GetStdHandle(stream);
            if (GetFileType(handle) != 3) throw new InvalidOperationException("Caller output must be a pipe.");
            if (!SetHandleInformation(handle, 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return CliConductorCommand.Run(["conductor", "start"], OrchestratorWorkspace.ForDirectory(root),
            launcher: new FakeCommandLauncher(root, token), jobProbe: ProcessJobTrapProbe.UnknownProbe);
    }

    private sealed class FakeCommandLauncher(string root, string token) : IConductorProcessLauncher
    {
        public ConductorLaunchResult Launch(ConductorLaunchRequest request)
        {
            var result = new HandleListConductorProcessLauncher().Launch(request with
            {
                FileName = Environment.ProcessPath!,
                Arguments = [typeof(ConductorStartCallerProbe).Assembly.Location, "fake-conductor", token, "60", root]
            });
            File.WriteAllText(Path.Combine(root, "launch-receipt.json"), result.StandardOutput ?? "{}");
            if (result.ExitCode == 0)
            {
                using var receipt = JsonDocument.Parse(result.StandardOutput!);
                using var process = Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32());
                File.WriteAllText(Path.Combine(root, "launch-identity.json"), JsonSerializer.Serialize(new
                {
                    pid = process.Id,
                    startedAt = process.StartTime.ToUniversalTime(),
                    imagePath = process.MainModule!.FileName
                }));
                // Keep the actual launching process and its console alive while the
                // fake captures membership; caller exit must not hide inheritance.
                using var ready = EventWaitHandle.OpenExisting(token);
                if (!ready.WaitOne(TimeSpan.FromMinutes(1)))
                    throw new TimeoutException("Fake conductor did not capture its console and flush its logs.");
            }
            return result;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int stream);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int stream, IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
}
