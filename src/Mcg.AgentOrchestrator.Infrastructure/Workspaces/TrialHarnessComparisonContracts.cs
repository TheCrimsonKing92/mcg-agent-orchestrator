using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface ITrialRootHost
{
    ITrialRootSession Create(TrialRootRequest request);
}

internal interface ITrialRootSession : IDisposable
{
    string RootPath { get; }

    string ResolvedBaseCommit { get; }

    string HarnessStatePath { get; }

    ITrialLaunch Start(ProcessStartInfo command);

    TrialTeardownReport Destroy();
}

internal interface ITrialLaunch : IDisposable
{
    string StdoutPath { get; }

    string StderrPath { get; }

    bool WaitForExit(int milliseconds);

    int ExitCode { get; }
}

internal sealed record TrialHarnessSpec(
    string Name,
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? Environment = null);

internal sealed record TrialComparisonRequest(
    string SourceRepositoryPath,
    string BaseCommit,
    IReadOnlyList<TrialHarnessSpec> Harnesses,
    string ReceiptsDirectory,
    string? TrialBaseDirectory = null,
    IReadOnlyList<string>? ProtectedPaths = null,
    TimeSpan? LaunchTimeout = null);

internal enum TrialHarnessOutcome
{
    NotAttempted,
    Completed,
    LaunchFailed,
    ReceiptCaptureFailed,
    TimedOut,
    ProtectedPathModified,
    TeardownUnclean
}

internal sealed record TrialHarnessResult(
    string Name,
    string ResolvedBaseCommit,
    string StdoutPath,
    string StderrPath,
    string ReceiptPath,
    string? TeardownReceiptPath,
    int? ExitCode,
    TrialHarnessOutcome Outcome,
    IReadOnlyList<string> Diagnostics);

internal sealed record TrialComparisonResult(
    string RequestedBaseCommit,
    string ResolvedBaseCommit,
    string ReceiptDirectory,
    string ReceiptPath,
    IReadOnlyList<TrialHarnessResult> Harnesses,
    IReadOnlyList<string> Failures,
    bool Succeeded);
