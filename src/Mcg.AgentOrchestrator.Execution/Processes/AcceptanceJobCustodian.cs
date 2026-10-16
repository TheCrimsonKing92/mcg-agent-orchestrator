using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A separate Windows handle owner survives both the conductor and acceptance root.
// Job completion notifications gate settlement; no process ancestry or elapsed-time guess is used.
internal static class AcceptanceJobCustodian
{
    internal static Process Start(string name, SpawnProcessIdentity holder, string? settlementPath)
    {
        if (settlementPath is not null && File.Exists(settlementPath))
            throw new InvalidOperationException("Acceptance job already has a settlement receipt.");
        var receipt = JsonSerializer.Serialize(new AcceptanceJobSettlement(name, holder, true));
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "$ErrorActionPreference='Stop';Add-Type -TypeDefinition @'\n" + NativeSource +
            "\n'@;[AcceptanceCustodian]::Run(" + Literal(name) + "," +
            Literal(settlementPath ?? "") + "," + Literal(receipt) + ")";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) startInfo.ArgumentList.Add(arg);
        var process = ProcessTreeGuiSuppression.Start(startInfo)
            ?? throw new InvalidOperationException("Acceptance custodian did not start.");
        try
        {
            var ready = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
                .GetAwaiter().GetResult();
            if (ready != "custody-ready")
                throw new InvalidOperationException("Acceptance custodian could not acquire job custody: " +
                    process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            return process;
        }
        catch
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            finally { process.Dispose(); }
            throw;
        }
    }

    // Windows PowerShell is a separate .NET Framework host; this small native bridge uses only
    // kernel32 and System.IO. A completion port stays exclusively owned by this custodian.
    private const string NativeSource = """
        using System;
        using System.IO;
        using System.ComponentModel;
        using System.Runtime.InteropServices;
        public static class AcceptanceCustodian
        {
            [StructLayout(LayoutKind.Sequential)] struct Association { public IntPtr Key, Port; }
            [StructLayout(LayoutKind.Sequential)] struct Accounting
            {
                public long User, Kernel, PeriodUser, PeriodKernel;
                public uint Faults, Total, Active, Terminated;
            }
            [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
            static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
            [DllImport("kernel32.dll", SetLastError=true)]
            static extern IntPtr CreateIoCompletionPort(IntPtr file, IntPtr existing, IntPtr key, uint threads);
            [DllImport("kernel32.dll", SetLastError=true)]
            static extern bool SetInformationJobObject(IntPtr job, int kind, ref Association info, uint length);
            [DllImport("kernel32.dll", SetLastError=true)]
            static extern bool QueryInformationJobObject(IntPtr job, int kind, out Accounting info, uint length, IntPtr returned);
            [DllImport("kernel32.dll", SetLastError=true)]
            static extern bool GetQueuedCompletionStatus(IntPtr port, out uint message, out IntPtr key, out IntPtr value, uint timeout);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
            static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            public static void Run(string name, string path, string receipt)
            {
                IntPtr job = OpenJobObject(2 | 4, false, name);
                Check(job != IntPtr.Zero);
                IntPtr port = IntPtr.Zero;
                try
                {
                    port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, IntPtr.Zero, 1);
                    Check(port != IntPtr.Zero);
                    var association = new Association { Key = new IntPtr(1), Port = port };
                    Check(SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf(typeof(Association))));
                    Console.WriteLine("custody-ready");
                    Console.Out.Flush();
                    // EOF after parent death also triggers accounting. No gate ran before assignment.
                    Console.ReadLine();
                    for (;;)
                    {
                        Accounting info;
                        Check(QueryInformationJobObject(job, 1, out info, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero));
                        if (info.Active == 0) break;
                        uint message; IntPtr key, value;
                        Check(GetQueuedCompletionStatus(port, out message, out key, out value, uint.MaxValue));
                    }
                    if (path.Length != 0)
                    {
                        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { File.WriteAllText(temporary, receipt); File.Move(temporary, path); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                }
                finally { if (port != IntPtr.Zero) CloseHandle(port); CloseHandle(job); }
            }
        }
        """;
}
