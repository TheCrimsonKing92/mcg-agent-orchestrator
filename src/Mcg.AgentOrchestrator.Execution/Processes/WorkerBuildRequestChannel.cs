using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerBuildHostReference(string StatePath, string ExecutionDirectory,
    string GoalId, string TaskId, DateTimeOffset DispatchAt, string DispatchId,
    TaskIntentAdmissionReference? Admission = null, string? StorageRoot = null);
public sealed record WorkerBuildRequest(int Version, string RequestKey, string[] Projects, string Configuration);
public sealed record WorkerBuildResponse(string InvocationId, WorkerBuildOutcome? Outcome, string Diagnostic)
{
    public int ExitCode => Outcome == WorkerBuildOutcome.Success ? 0 : 1;
}

public interface IWorkerBuildRequestService
{
    Task<WorkerBuildResponse> ExecuteAsync(WorkerBuildRequest request, WorkerBuildProcess worker,
        CancellationToken workerLifetime);
}

public interface IWorkerBuildInvocationOperations
{
    WorkerBuildHostReference Prepare(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId);
    bool ReconcileOutstanding(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, bool cancelled = false);
    WorkerBuildConsumption Consume(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId,
        IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths);
}

public sealed record WorkerBuildConsumption(bool MissingEvidence, bool CompilerFailed, string Diagnostic);

// The file locates a dispatch-scoped endpoint. Only an OS-verified peer in the owned job is admitted.
internal sealed class WorkerBuildRequestChannel : IDisposable
{
    internal const string DescriptorRelativePath = ".orchestrator-context/worker-build-channel.json";
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IWorkerBuildRequestService _service;
    private readonly TaskCompletionSource<(Process Worker, OwnedProcessGroup Group)> _worker =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serve;
    private readonly FileStream _descriptorLease;
    private readonly Action<int, bool>? _observePeer;

    internal WorkerBuildRequestChannel(string root, IWorkerBuildRequestService service, Action<int, bool>? observePeer = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Worker build channel requires Windows peer custody.");
        _service = service;
        _observePeer = observePeer;
        var name = "mcg-worker-build-" + Guid.NewGuid().ToString("N");
        _pipe = CreateServer(name);
        try
        {
            var descriptor = WorkerBuildProjectScope.ValidatePath(root, Path.Combine(root, DescriptorRelativePath), mustExist: false);
            Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
            // Never overwrite an existing worker-controlled file/hardlink. Keep the locator locked,
            // and delete the exact opened object on close instead of re-resolving a worker-owned path.
            _descriptorLease = new FileStream(descriptor, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.DeleteOnClose | FileOptions.WriteThrough);
            _descriptorLease.Write(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, pipe = name }));
            _descriptorLease.Flush(flushToDisk: true);
        }
        catch { _descriptorLease?.Dispose(); _pipe.Dispose(); throw; }
        _serve = ServeAsync();
    }

    internal void Register(Process worker, OwnedProcessGroup group) => _worker.SetResult((worker, group));

    private async Task ServeAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await _pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                var owner = await _worker.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var pid))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                using var peer = Process.GetProcessById(checked((int)pid));
                // Opening the handle pins the identity against PID reuse before testing job membership.
                var identity = ReadIdentity(peer);
                var admitted = !owner.Worker.HasExited && owner.Group.Contains(peer.SafeHandle);
                _observePeer?.Invoke(peer.Id, admitted);
                if (!admitted)
                    throw new UnauthorizedAccessException("Worker build peer is outside this dispatch's owned job.");
                using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                using var admission = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                admission.CancelAfter(TimeSpan.FromSeconds(15));
                var message = new StringBuilder();
                var buffer = new char[1];
                while (true)
                {
                    if (await reader.ReadAsync(buffer.AsMemory(), admission.Token).ConfigureAwait(false) == 0)
                        throw new IOException("Worker build request ended before its frame.");
                    if (buffer[0] == '\n') break;
                    if (message.Length == 32768) throw new IOException("Worker build request exceeds frame limit.");
                    message.Append(buffer[0]);
                }
                var request = JsonSerializer.Deserialize<WorkerBuildRequest>(message.ToString())
                    ?? throw new IOException("Worker build request is empty.");
                if (request.Version != 1 || !Guid.TryParseExact(request.RequestKey, "N", out _) ||
                    request.Projects is not { Length: > 0 and <= 100 } ||
                    request.Configuration is not ("Debug" or "Release"))
                    throw new UnauthorizedAccessException("Unsupported worker build request.");
                if (peer.HasExited || !owner.Group.Contains(peer.SafeHandle) || ReadIdentity(peer) != identity)
                    throw new UnauthorizedAccessException("Worker build peer identity changed.");
                var response = await _service.ExecuteAsync(request, ReadIdentity(owner.Worker), _lifetime.Token)
                    .ConfigureAwait(false);
                // Disconnect is transport loss, not cancellation or evidence about compiler execution.
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false); }
                catch (IOException) { }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Rejected frames carry no authority and cannot launch anything. The helper sees EOF.
            }
            finally { if (_pipe.IsConnected) _pipe.Disconnect(); }
        }
    }

    internal static WorkerBuildProcess ReadIdentity(Process process)
    {
        var read = WindowsNativeProcessInspection.ReadLifecycleIdentity(process.Id, process.SafeHandle);
        if (read.Status != ProcessInspectionStatus.Available || read.StartedAt is not { } at ||
            string.IsNullOrWhiteSpace(read.ExecutablePath))
            throw new UnauthorizedAccessException("Worker build process identity is unavailable.");
        return new(process.Id, at, read.ExecutablePath);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        // The host must resolve outstanding compiler custody before publishing worker completion.
        if (!_serve.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Worker build terminal reconciliation did not finish.");
        _pipe.Dispose();
        _lifetime.Dispose();
        _descriptorLease.Dispose();
    }

    private static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var logon = ReadLogonSid(identity.AccessToken);
        // Read/write data and synchronization, deliberately excluding FILE_CREATE_PIPE_INSTANCE (0x4).
        // A user-wide generic-write ACE would let the same-user worker create another server instance.
        var sddl = $"D:P(A;;GA;;;SY)(A;;0x0012019b;;;{logon})S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + name, 0x40080003, 0x8, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { _ = LocalFree(sd); }
    }

    private static string ReadLogonSid(SafeAccessTokenHandle token)
    {
        // WindowsIdentity.Groups deliberately filters out SE_GROUP_LOGON_ID entries.
        // Query the token's logon SID directly; never widen the pipe ACE to the user SID.
        const int tokenLogonSid = 28;
        const uint logonId = 0xc0000000;
        _ = GetTokenInformation(token, tokenLogonSid, IntPtr.Zero, 0, out var length);
        var error = Marshal.GetLastWin32Error();
        if (error != 122) throw new Win32Exception(error);
        if (length < Marshal.SizeOf<TokenLogonGroups>())
            throw new UnauthorizedAccessException("Worker build channel requires a logon SID.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, tokenLogonSid, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var groups = Marshal.PtrToStructure<TokenLogonGroups>(buffer);
            if (groups.Count != 1 || groups.Group.Sid == IntPtr.Zero ||
                (groups.Group.Attributes & logonId) != logonId)
                throw new UnauthorizedAccessException("Worker build channel requires exactly one token logon SID.");
            var sid = new SecurityIdentifier(groups.Group.Sid);
            if (!sid.IsWellKnown(WellKnownSidType.LogonIdsSid))
                throw new UnauthorizedAccessException("Worker build channel token SID is not a logon SID.");
            return sid.Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenLogonGroups { public uint Count; public SidAndAttributes Group; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, int length, out int needed);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint mode, uint instances,
        uint outSize, uint inSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
