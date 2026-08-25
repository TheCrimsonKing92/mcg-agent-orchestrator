using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

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

    void AddEnvironment(IReadOnlyDictionary<string, string?> environment);

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
    TrialWorkload Workload,
    IReadOnlyList<TrialHarnessSpec> Harnesses,
    string ReceiptsDirectory,
    string? TrialBaseDirectory = null,
    IReadOnlyList<string>? ProtectedPaths = null,
    TimeSpan? LaunchTimeout = null);

internal sealed record TrialWorkload(
    string BriefIdentity,
    string BriefContent,
    string BriefDigest,
    string ModelIdentity,
    string SourceProvenance,
    GoalTimingReportSnapshot? HistoricalTiming = null);

internal sealed record TrialWorkloadIdentity(
    string Value,
    string BriefIdentity,
    string BriefDigest,
    string ResolvedBaseCommit,
    string ModelIdentity,
    string SourceProvenance);

internal sealed record TrialArmIdentity(
    string Value,
    string WorkloadIdentity,
    string HarnessIdentity);

internal static class TrialIdentity
{
    public const string EnvironmentPrefix = "MCG_TRIAL_";

    public static string ComputeBriefDigest(string briefContent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(briefContent))).ToLowerInvariant();

    public static TrialWorkloadIdentity CreateWorkload(TrialWorkload workload, string resolvedBaseCommit)
    {
        var value = ComputeIdentifier(
            "trial-workload-v1",
            workload.BriefIdentity,
            workload.BriefDigest,
            resolvedBaseCommit,
            workload.ModelIdentity);
        return new TrialWorkloadIdentity(
            value,
            workload.BriefIdentity,
            workload.BriefDigest,
            resolvedBaseCommit,
            workload.ModelIdentity,
            workload.SourceProvenance);
    }

    public static TrialArmIdentity CreateArm(TrialWorkloadIdentity workload, string harnessIdentity) =>
        new(
            ComputeIdentifier("trial-arm-v1", workload.Value, harnessIdentity),
            workload.Value,
            harnessIdentity);

    private static string ComputeIdentifier(params string[] fields)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

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
    IReadOnlyList<string> Diagnostics,
    TrialWorkloadIdentity? WorkloadIdentity,
    TrialArmIdentity? ArmIdentity,
    GoalTimingReportSnapshot? HistoricalTiming);

internal sealed record TrialComparisonResult(
    string RequestedBaseCommit,
    string ResolvedBaseCommit,
    string ReceiptDirectory,
    string ReceiptPath,
    IReadOnlyList<TrialHarnessResult> Harnesses,
    IReadOnlyList<string> Failures,
    bool Succeeded,
    TrialWorkloadIdentity? WorkloadIdentity,
    GoalTimingReportSnapshot? HistoricalTiming);
