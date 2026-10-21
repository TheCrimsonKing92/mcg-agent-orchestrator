using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceJobCustodianNative
{
    // Runs only in App's internal process function, outside the job being held.
    internal static int Run(TextReader input, TextWriter output, TextWriter error)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Custody requires Windows.");
            var line = AcceptanceJobCustodianProtocol.ReadLineAsync(input).WaitAsync(TimeSpan.FromSeconds(30))
                .GetAwaiter().GetResult();
            var request = line is null ? null : JsonSerializer.Deserialize<AcceptanceJobCustodianProtocol>(line);
            if (request is null || request.Phase != "request" || string.IsNullOrWhiteSpace(request.JobName) ||
                request.Holder is null || request.Custodian is not null ||
                (request.SettlementPath is not null && !Path.IsPathFullyQualified(request.SettlementPath)))
                throw new InvalidOperationException("Invalid custody request.");
            var holder = request.Holder;
            var entry = new SpawnRegistryEntry(0, "acceptance-holder", holder.ProcessId, holder.StartedAt,
                holder.ImagePath, holder.StartedAt, null, null, null, null, null, SpawnRegistryLifecycle.Owned);
            var status = SpawnProcessIdentityReader.EvaluateTrackedProcess(entry, out var process, out _);
            using var holderProcess = process;
            if (status != SpawnTrackedProcessStatus.LiveMatch || process is null)
                throw new InvalidOperationException("Acceptance holder identity mismatch.");
            using var self = Process.GetCurrentProcess();
            var identity = SpawnProcessIdentityReader.ReadForRegistration(self).Identity
                ?? throw new InvalidOperationException("Acceptance custodian identity is unavailable.");
            using var job = OpenJobObject(2 | 4, false, request.JobName);
            Check(!job.IsInvalid);
            Check(IsProcessInJob(self.Handle, job, out var selfInJob));
            if (selfInJob) throw new InvalidOperationException("Custodian cannot own its containing job.");
            using var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, IntPtr.Zero, 1);
            Check(!port.IsInvalid);
            var association = new Association { Key = new IntPtr(1), Port = port.DangerousGetHandle() };
            Check(SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<Association>()));
            var ready = request with { Phase = "ready", Custodian = identity };
            output.WriteLine(JsonSerializer.Serialize(ready));
            output.Flush();
            var attach = AcceptanceJobCustodianProtocol.ReadLineAsync(input).WaitAsync(TimeSpan.FromSeconds(30))
                .GetAwaiter().GetResult();
            // EOF before attachment is a startup failure, never successful empty-job settlement.
            AcceptanceJobCustodianProtocol.Validate(attach, "attach", request, identity);
            Check(QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero));
            Check(IsProcessInJob(process.Handle, job, out var holderInJob));
            if (!holderInJob || accounting.Total == 0)
                throw new InvalidOperationException("Acceptance holder was not assigned before attachment.");
            output.WriteLine(JsonSerializer.Serialize(ready with { Phase = "attached" }));
            output.Flush();
            for (;;)
            {
                Check(QueryInformationJobObject(job, 1, out accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero));
                if (accounting.Active == 0) break;
                Check(GetQueuedCompletionStatus(port, out _, out _, out _, uint.MaxValue));
            }
            if (request.SettlementPath is { } path)
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.WriteThrough))
                    {
                        JsonSerializer.Serialize(stream, new AcceptanceJobSettlement(request.JobName, holder, true));
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return 0;
        }
        catch (Exception ex) { error.WriteLine($"Acceptance custody failed: {ex.Message}"); error.Flush(); return 1; }
    }

    private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct Association { public IntPtr Key, Port; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint Faults, Total, Active, Terminated;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateIoCompletionPort(IntPtr file, IntPtr existing, IntPtr key, uint threads);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref Association info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out IntPtr key, out IntPtr value, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, out bool inJob);
}
