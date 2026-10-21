using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Startup owns no lasting job handle. The child owns native lifetime custody.
internal sealed class AcceptanceJobCustodian : IDisposable
{
    private readonly Process _process;
    private readonly AcceptanceJobCustodianProtocol _protocol;
    private readonly AcceptanceJobCustodianBounds _bounds;
    private readonly AcceptanceJobCustodianDiagnostics _diagnostics;
    private readonly Task _drain;
    internal SpawnProcessIdentity Identity { get; }

    private AcceptanceJobCustodian(Process process, AcceptanceJobCustodianProtocol protocol,
        AcceptanceJobCustodianBounds bounds, AcceptanceJobCustodianDiagnostics diagnostics, Task drain)
    {
        _process = process;
        _protocol = protocol;
        _bounds = bounds;
        _diagnostics = diagnostics;
        _drain = drain;
        Identity = SpawnProcessIdentityReader.ReadForRegistration(process).Identity
            ?? throw new InvalidOperationException("Acceptance custodian identity is unavailable.");
    }

    internal static AcceptanceJobCustodian Start(AcceptanceJobCustodianLaunchSpecification launch,
        string name, SpawnProcessIdentity holder, string? settlementPath,
        AcceptanceJobCustodianBounds? bounds = null)
    {
        if (settlementPath is not null && File.Exists(settlementPath))
            throw new InvalidOperationException("Acceptance job already has a settlement receipt.");
        var process = ProcessTreeGuiSuppression.Start(launch.CreateStartInfo())
            ?? throw new InvalidOperationException("Acceptance custodian did not start.");
        var diagnostics = new AcceptanceJobCustodianDiagnostics();
        var drain = diagnostics.DrainAsync(process.StandardError);
        var limits = bounds ?? new();
        AcceptanceJobCustodian? session = null;
        try
        {
            session = new(process, new("request", name, holder, null, settlementPath), limits, diagnostics, drain);
            process.StandardInput.WriteLine(JsonSerializer.Serialize(session._protocol));
            process.StandardInput.Flush();
            session.Expect("ready");
            return session;
        }
        catch (Exception ex)
        {
            if (session is not null) session.Dispose();
            else
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                    try { drain.WaitAsync(limits.DiagnosticDrain, limits.TimeProvider).GetAwaiter().GetResult(); }
                    catch (TimeoutException) { }
                    throw new InvalidOperationException($"Acceptance custodian startup failed: {ex.Message}; stderr: {diagnostics.Snapshot()}", ex);
                }
                finally { process.Dispose(); }
            }
            throw;
        }
    }

    internal void Attached()
    {
        _process.StandardInput.WriteLine(JsonSerializer.Serialize(_protocol with
            { Phase = "attach", Custodian = Identity }));
        _process.StandardInput.Close();
        Expect("attached");
    }

    private void Expect(string phase)
    {
        try
        {
            var line = _bounds.ReadLineAsync(_process.StandardOutput).GetAwaiter().GetResult();
            if (line is null)
                throw new EndOfStreamException("Acceptance custodian closed its protocol stream before " + phase + ".");
            AcceptanceJobCustodianProtocol.Validate(line, phase, _protocol, Identity);
            if (_process.HasExited)
                throw new InvalidOperationException("Acceptance custodian exited during attachment.");
        }
        catch (Exception ex)
        {
            var exit = _process.HasExited ? _process.ExitCode.ToString() : "unconfirmed";
            Stop();
            try { _drain.WaitAsync(_bounds.DiagnosticDrain, _bounds.TimeProvider).GetAwaiter().GetResult(); }
            catch (TimeoutException) { }
            throw new InvalidOperationException($"Acceptance custodian {phase} failed: {ex.Message}; observed_exit={exit}; failure={ex.GetType().Name}; stderr: {_diagnostics.Snapshot()}", ex);
        }
    }

    // Drain both streams until exit, without retaining a native job handle.
    internal void Release() => _ = DrainUntilExitAsync();
    private async Task DrainUntilExitAsync()
    {
        try
        {
            await Task.WhenAll(_diagnostics.DrainAsync(_process.StandardOutput), _drain,
                _process.WaitForExitAsync()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        finally { _process.Dispose(); }
    }

    private void Stop()
    {
        if (!_process.HasExited) _process.Kill();
        _process.WaitForExitAsync().WaitAsync(_bounds.DiagnosticDrain, _bounds.TimeProvider)
            .GetAwaiter().GetResult();
    }
    public void Dispose() { try { Stop(); } finally { _process.Dispose(); } }
}

internal sealed record AcceptanceJobCustodianBounds
{
    internal TimeSpan Readiness { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan DiagnosticDrain { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    internal Task<string?> ReadLineAsync(TextReader reader) => AcceptanceJobCustodianProtocol.ReadLineAsync(reader)
        .WaitAsync(Readiness, TimeProvider);
}

internal sealed record AcceptanceJobCustodianProtocol(string Phase, string JobName,
    SpawnProcessIdentity Holder, SpawnProcessIdentity? Custodian, string? SettlementPath)
{
    internal static async Task<string?> ReadLineAsync(TextReader reader)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == 16384) throw new InvalidOperationException("Custody protocol line exceeds its bound.");
            line.Append(buffer[0]);
        }
        return line.Length == 0 ? null : throw new InvalidOperationException("Incomplete custody protocol line.");
    }

    internal static void Validate(string? line, string phase, AcceptanceJobCustodianProtocol expected,
        SpawnProcessIdentity custodian)
    {
        var actual = line is null ? null : JsonSerializer.Deserialize<AcceptanceJobCustodianProtocol>(line);
        if (actual is null || actual.Phase != phase || actual.JobName != expected.JobName ||
            actual.SettlementPath != expected.SettlementPath || actual.Holder is null || actual.Custodian is null ||
            !ProcessImageResolution.SameProcess(actual.Holder, expected.Holder) ||
            !ProcessImageResolution.SameProcess(actual.Custodian, custodian))
            throw new InvalidOperationException("Acceptance custodian protocol identity mismatch.");
    }
}

internal sealed class AcceptanceJobCustodianDiagnostics
{
    internal const int Capacity = 8192;
    private readonly StringBuilder _tail = new();
    internal async Task DrainAsync(TextReader reader)
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
                lock (_tail)
                {
                    _tail.Append(buffer, 0, count);
                    if (_tail.Length > Capacity) _tail.Remove(0, _tail.Length - Capacity);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    internal string Snapshot() { lock (_tail) return _tail.ToString(); }
}
