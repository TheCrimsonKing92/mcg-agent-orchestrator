using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class SqliteWriteTelemetryOptions
{
    public TimeSpan WarningHoldThreshold { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan CriticalHoldThreshold { get; init; } = TimeSpan.FromSeconds(2);
    public int BusyTimeoutMilliseconds { get; init; } = 30_000;
    public TimeSpan BusyRetryBudget { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxBusyRetries { get; init; } = 6;
    public int? BeginImmediateCommandTimeoutSeconds { get; init; }
    public string? DiagnosticsPath { get; init; }
    public bool? MirrorToConductEventStream { get; init; }
    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<long> MonotonicMilliseconds { get; init; } = () => Environment.TickCount64;
    public Func<int, TimeSpan, CancellationToken, Task>? RetryDelay { get; init; }

    public static SqliteWriteTelemetryOptions FromEnvironment() =>
        new()
        {
            WarningHoldThreshold = ReadMilliseconds("MCG_STATE_WRITE_WARNING_MS", TimeSpan.FromMilliseconds(500)),
            CriticalHoldThreshold = ReadMilliseconds("MCG_STATE_WRITE_CRITICAL_MS", TimeSpan.FromSeconds(2)),
            DiagnosticsPath = EmptyToNull(Environment.GetEnvironmentVariable("MCG_STATE_WRITE_DIAGNOSTICS_PATH"))
        };

    private static TimeSpan ReadMilliseconds(string name, TimeSpan fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds >= 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : fallback;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed class SqliteWriteTelemetry
{
    public const string DiagnosticsFileName = "state-write-transactions.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object FileLock = new();

    private readonly string _dbPath;
    private readonly SqliteWriteTelemetryOptions _options;
    private readonly string _diagnosticsPath;
    private readonly string _conductEventsPath;
    private readonly bool _mirrorToConductEventStream;

    public SqliteWriteTelemetry(string dbPath, SqliteWriteTelemetryOptions? options = null)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _options = options ?? SqliteWriteTelemetryOptions.FromEnvironment();
        var logDirectory = Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", "logs");
        _diagnosticsPath = _options.DiagnosticsPath ?? Path.Combine(logDirectory, DiagnosticsFileName);
        _conductEventsPath = Path.Combine(logDirectory, "conduct-events.log");
        _mirrorToConductEventStream = _options.MirrorToConductEventStream ?? IsConductLoopProcess();
    }

    public SqliteWriteTelemetryOptions Options => _options;

    public WriteTelemetryScope StartScope(string operation, TimeSpan acquisitionWait) =>
        new(this, operation, acquisitionWait);

    public void EmitBusyFailure(
        string operation,
        TimeSpan acquisitionWait,
        Exception exception,
        int attemptCount)
    {
        var receipt = BuildReceipt(
            eventType: "sqlite-state-write-busy-failure",
            severity: "critical",
            operation,
            disposition: "busy-failure",
            acquisitionWait,
            holdDuration: null,
            rowsWritten: 0,
            serializedBytes: 0,
            exception,
            includeStack: true,
            sqliteException: exception as Microsoft.Data.Sqlite.SqliteException,
            attemptCount,
            maxAttempts: _options.MaxBusyRetries);
        AppendReceipt(receipt);
    }

    public void EmitBusyRetry(
        string operation,
        TimeSpan elapsed,
        Microsoft.Data.Sqlite.SqliteException exception,
        int attemptCount)
    {
        AppendReceipt(BuildReceipt(
            eventType: "sqlite-state-write-lock",
            severity: "warning",
            operation,
            disposition: "retrying",
            acquisitionWait: elapsed,
            holdDuration: null,
            rowsWritten: 0,
            serializedBytes: 0,
            exception,
            includeStack: false,
            sqliteException: exception,
            attemptCount,
            maxAttempts: _options.MaxBusyRetries));
    }

    public void EmitBusyRecovered(
        string operation,
        TimeSpan elapsed,
        Microsoft.Data.Sqlite.SqliteException exception,
        int attemptCount)
    {
        AppendReceipt(BuildReceipt(
            eventType: "sqlite-state-write-lock",
            severity: "warning",
            operation,
            disposition: "recovered",
            acquisitionWait: elapsed,
            holdDuration: null,
            rowsWritten: 0,
            serializedBytes: 0,
            exception,
            includeStack: false,
            sqliteException: exception,
            attemptCount,
            maxAttempts: _options.MaxBusyRetries));
    }

    private void EmitCompletedScope(
        string operation,
        string disposition,
        TimeSpan acquisitionWait,
        TimeSpan holdDuration,
        long rowsWritten,
        long serializedBytes,
        Exception? exception)
    {
        var severity = SeverityForHold(holdDuration);
        if (severity is null)
            return;

        var receipt = BuildReceipt(
            eventType: "sqlite-state-write-hold",
            severity,
            operation,
            disposition,
            acquisitionWait,
            holdDuration,
            rowsWritten,
            serializedBytes,
            exception,
            includeStack: string.Equals(severity, "critical", StringComparison.Ordinal),
            sqliteException: exception as Microsoft.Data.Sqlite.SqliteException,
            attemptCount: 1,
            maxAttempts: _options.MaxBusyRetries);
        AppendReceipt(receipt);
    }

    private string? SeverityForHold(TimeSpan holdDuration)
    {
        if (holdDuration > _options.CriticalHoldThreshold)
            return "critical";
        if (holdDuration > _options.WarningHoldThreshold)
            return "warning";
        return null;
    }

    private SqliteWriteTelemetryReceipt BuildReceipt(
        string eventType,
        string severity,
        string operation,
        string disposition,
        TimeSpan acquisitionWait,
        TimeSpan? holdDuration,
        long rowsWritten,
        long serializedBytes,
        Exception? exception,
        bool includeStack,
        Microsoft.Data.Sqlite.SqliteException? sqliteException,
        int attemptCount,
        int maxAttempts)
    {
        return new SqliteWriteTelemetryReceipt(
            Timestamp: _options.UtcNow(),
            EventType: eventType,
            Severity: severity,
            StateDbPath: _dbPath,
            Operation: operation,
            ProcessId: Environment.ProcessId,
            ProcessRole: ResolveProcessRole(),
            Store: "state",
            Disposition: disposition,
            SqliteErrorCode: sqliteException?.SqliteErrorCode,
            SqliteExtendedErrorCode: sqliteException?.SqliteExtendedErrorCode,
            AttemptCount: attemptCount,
            MaxAttempts: maxAttempts,
            AcquisitionWaitMs: RoundMilliseconds(acquisitionWait),
            HoldMs: holdDuration is null ? null : RoundMilliseconds(holdDuration.Value),
            RowsWritten: rowsWritten,
            SerializedBytes: serializedBytes,
            ExceptionType: exception?.GetType().FullName,
            Error: exception?.Message,
            StackSummary: includeStack ? BuildStackSummary() : null);
    }

    private void AppendReceipt(SqliteWriteTelemetryReceipt receipt)
    {
        try
        {
            var line = JsonSerializer.Serialize(receipt, JsonOptions);
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticsPath) ?? ".");
                File.AppendAllText(_diagnosticsPath, line + Environment.NewLine);

                if (_mirrorToConductEventStream &&
                    (string.Equals(receipt.Severity, "warning", StringComparison.Ordinal) ||
                     string.Equals(receipt.Severity, "critical", StringComparison.Ordinal)))
                {
                    AppendConductEvent(receipt);
                }
            }
        }
        catch
        {
            // Diagnostics must never affect SQLite transaction behavior.
        }
    }

    private void AppendConductEvent(SqliteWriteTelemetryReceipt receipt)
    {
        var detail =
            $"SQLITE_WRITE_TELEMETRY severity={receipt.Severity} event={receipt.EventType} operation=\"{receipt.Operation}\" " +
            $"store={receipt.Store} database=\"{receipt.StateDbPath}\" disposition={receipt.Disposition} " +
            $"sqlite_code={(receipt.SqliteErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none")} " +
            $"sqlite_extended_code={(receipt.SqliteExtendedErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none")} " +
            $"attempt={receipt.AttemptCount} max_attempts={receipt.MaxAttempts} elapsed_ms={receipt.AcquisitionWaitMs.ToString(CultureInfo.InvariantCulture)} " +
            $"hold_ms={(receipt.HoldMs?.ToString(CultureInfo.InvariantCulture) ?? "null")} pid={receipt.ProcessId}";
        var record = new ConductEventMirrorRecord(
            receipt.Timestamp,
            "sqlite-write-telemetry",
            GoalId: null,
            detail);
        Directory.CreateDirectory(Path.GetDirectoryName(_conductEventsPath) ?? ".");
        File.AppendAllText(_conductEventsPath, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
    }

    private static double RoundMilliseconds(TimeSpan value) =>
        Math.Round(value.TotalMilliseconds, 3, MidpointRounding.AwayFromZero);

    private static string ResolveProcessRole()
    {
        var overrideValue = Environment.GetEnvironmentVariable("MCG_STATE_WRITE_PROCESS_ROLE");
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return overrideValue.Trim();

        var commandLine = Environment.CommandLine;
        if (commandLine.Contains("conduct", StringComparison.OrdinalIgnoreCase) &&
            commandLine.Contains("--loop", StringComparison.OrdinalIgnoreCase))
        {
            return "loop";
        }

        if (commandLine.Contains("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
            commandLine.Contains("dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return "dashboard";
        }

        return "cli";
    }

    private static bool IsConductLoopProcess()
    {
        var commandLine = Environment.CommandLine;
        return commandLine.Contains("conduct", StringComparison.OrdinalIgnoreCase) &&
               commandLine.Contains("--loop", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildStackSummary()
    {
        var trace = new StackTrace(skipFrames: 2, fNeedFileInfo: false);
        return trace
            .GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}")
            .Where(name =>
                !name.Contains(nameof(SqliteWriteTelemetry), StringComparison.Ordinal) &&
                !name.Contains(nameof(WriteTelemetryScope), StringComparison.Ordinal))
            .Take(12)
            .ToArray();
    }

    public sealed class WriteTelemetryScope
    {
        private readonly SqliteWriteTelemetry _telemetry;
        private readonly Stopwatch _holdStopwatch = Stopwatch.StartNew();
        private long _rowsWritten;
        private long _serializedBytes;
        private bool _emitted;

        internal WriteTelemetryScope(SqliteWriteTelemetry telemetry, string operation, TimeSpan acquisitionWait)
        {
            _telemetry = telemetry;
            Operation = operation;
            AcquisitionWait = acquisitionWait;
        }

        public string Operation { get; }
        public TimeSpan AcquisitionWait { get; }

        public void AddWrite(int rowsWritten, long serializedBytes = 0)
        {
            if (rowsWritten > 0)
                _rowsWritten += rowsWritten;
            if (serializedBytes > 0)
                _serializedBytes += serializedBytes;
        }

        public void AddWrite((int RowsWritten, long SerializedBytes) write) =>
            AddWrite(write.RowsWritten, write.SerializedBytes);

        public void Emit(string disposition, Exception? exception = null)
        {
            if (_emitted)
                return;

            _emitted = true;
            _holdStopwatch.Stop();
            _telemetry.EmitCompletedScope(
                Operation,
                disposition,
                AcquisitionWait,
                _holdStopwatch.Elapsed,
                _rowsWritten,
                _serializedBytes,
                exception);
        }
    }
}

internal sealed record SqliteWriteTelemetryReceipt(
    DateTimeOffset Timestamp,
    string EventType,
    string Severity,
    string StateDbPath,
    string Operation,
    int ProcessId,
    string ProcessRole,
    string Store,
    string Disposition,
    int? SqliteErrorCode,
    int? SqliteExtendedErrorCode,
    int AttemptCount,
    int MaxAttempts,
    double AcquisitionWaitMs,
    double? HoldMs,
    long RowsWritten,
    long SerializedBytes,
    string? ExceptionType,
    string? Error,
    string[]? StackSummary);

internal sealed record ConductEventMirrorRecord(
    DateTimeOffset Timestamp,
    string EventKind,
    string? GoalId,
    string Detail);
