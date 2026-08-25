using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class TrialHarnessComparison(ITrialRootHost host)
{
    private const int MaximumWorkerResultInspectionBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TrialComparisonResult Run(TrialComparisonRequest request)
    {
        Validate(request);

        var runDirectory = Path.Combine(
            Path.GetFullPath(request.ReceiptsDirectory),
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        var historicalTimingReceipt = RedactHistoricalTiming(request.Workload.HistoricalTiming);
        var states = request.Harnesses
            .Select(spec => new HarnessState(
                spec,
                CreateHarnessReceiptPaths(runDirectory, spec.Name),
                historicalTimingReceipt))
            .ToArray();
        var created = new List<(HarnessState State, ITrialRootSession Session)>();
        var failures = new List<string>();
        TrialWorkloadIdentity? workloadIdentity = null;

        try
        {
            foreach (var state in states)
            {
                try
                {
                    var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    if (state.Spec.Environment is not null)
                    {
                        foreach (var pair in state.Spec.Environment)
                        {
                            environment[pair.Key] = pair.Value;
                        }
                    }

                    var session = host.Create(new TrialRootRequest(
                        request.SourceRepositoryPath,
                        request.BaseCommit,
                        request.TrialBaseDirectory,
                        state.Spec.Name,
                        request.ProtectedPaths,
                        environment));
                    created.Add((state, session));
                    state.ResolvedBaseCommit = session.ResolvedBaseCommit;
                }
                catch (Exception ex)
                {
                    state.Outcome = TrialHarnessOutcome.LaunchFailed;
                    state.Diagnostics.Add(ex.Message);
                    failures.Add($"Harness '{state.Spec.Name}' trial-root creation failed: {ex.Message}");
                    break;
                }
            }

            if (created.Count == states.Length)
            {
                var resolvedCommits = created
                    .Select(item => item.Session.ResolvedBaseCommit)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (resolvedCommits.Length != 1)
                {
                    failures.Add($"Trial roots resolved different base commits: {string.Join(", ", resolvedCommits)}.");
                }
                else
                {
                    workloadIdentity = TrialIdentity.CreateWorkload(request.Workload, resolvedCommits[0]);
                    foreach (var item in created)
                    {
                        try
                        {
                            ProvisionWorkload(item.State, item.Session, request.Workload, workloadIdentity);
                        }
                        catch (Exception ex)
                        {
                            item.State.Outcome = TrialHarnessOutcome.LaunchFailed;
                            item.State.Diagnostics.Add(ex.Message);
                            failures.Add($"Harness '{item.State.Spec.Name}' workload provisioning failed: {ex.Message}");
                        }
                    }

                    if (failures.Count == 0)
                    {
                        foreach (var item in created)
                        {
                            RunHarness(item.State, item.Session, request.LaunchTimeout ?? TimeSpan.FromMinutes(30), failures);
                        }
                    }
                }
            }
        }
        finally
        {
            for (var index = created.Count - 1; index >= 0; index--)
            {
                var (state, session) = created[index];
                try
                {
                    var report = session.Destroy();
                    state.TeardownReceiptPath = report.ReceiptPath;
                    if (report.OutsideWrites.Count > 0)
                    {
                        state.Outcome = TrialHarnessOutcome.ProtectedPathModified;
                        foreach (var path in report.OutsideWrites)
                        {
                            var failure = $"Harness '{state.Spec.Name}' modified protected path '{path}'.";
                            state.Diagnostics.Add(failure);
                            failures.Add(failure);
                        }
                    }
                    else if (!report.Clean)
                    {
                        MarkTeardownUnclean(state);
                        var failure = $"Harness '{state.Spec.Name}' trial-root teardown was unclean: {string.Join("; ", report.Diagnostics)}";
                        state.Diagnostics.Add(failure);
                        failures.Add(failure);
                    }
                }
                catch (Exception ex)
                {
                    MarkTeardownUnclean(state);
                    var failure = $"Harness '{state.Spec.Name}' trial-root teardown failed: {ex.Message}";
                    state.Diagnostics.Add(failure);
                    failures.Add(failure);
                }
                finally
                {
                    try
                    {
                        session.Dispose();
                    }
                    catch (Exception ex)
                    {
                        MarkTeardownUnclean(state);
                        var failure = $"Harness '{state.Spec.Name}' trial-root disposal failed: {ex.Message}";
                        state.Diagnostics.Add(failure);
                        failures.Add(failure);
                    }
                }
            }
        }

        var results = states.Select(ToResult).ToArray();
        foreach (var result in results)
        {
            File.WriteAllText(result.ReceiptPath, JsonSerializer.Serialize(result, ReceiptJson));
        }

        var comparisonReceiptPath = Path.Combine(runDirectory, "comparison.json");
        var comparison = new TrialComparisonResult(
            request.BaseCommit,
            results.Select(result => result.ResolvedBaseCommit).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            runDirectory,
            comparisonReceiptPath,
            results,
            failures.ToArray(),
            failures.Count == 0,
            workloadIdentity,
            historicalTimingReceipt);
        File.WriteAllText(comparisonReceiptPath, JsonSerializer.Serialize(comparison, ReceiptJson));
        return comparison;
    }

    private static void ProvisionWorkload(
        HarnessState state,
        ITrialRootSession session,
        TrialWorkload workload,
        TrialWorkloadIdentity workloadIdentity)
    {
        var armIdentity = TrialIdentity.CreateArm(workloadIdentity, state.Spec.Name);
        state.WorkloadIdentity = workloadIdentity;
        state.ArmIdentity = armIdentity;
        var briefPath = Path.Combine(session.HarnessStatePath, "trial-brief.utf8");
        File.WriteAllBytes(briefPath, Encoding.UTF8.GetBytes(workload.BriefContent));
        session.AddEnvironment(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MCG_TRIAL_BRIEF_PATH"] = briefPath,
            ["MCG_TRIAL_BRIEF_IDENTITY"] = workload.BriefIdentity,
            ["MCG_TRIAL_BRIEF_SHA256"] = workload.BriefDigest,
            ["MCG_TRIAL_BASE_COMMIT"] = workloadIdentity.ResolvedBaseCommit,
            ["MCG_TRIAL_MODEL_IDENTITY"] = workload.ModelIdentity,
            ["MCG_TRIAL_WORKLOAD_ID"] = workloadIdentity.Value,
            ["MCG_TRIAL_ARM_ID"] = armIdentity.Value,
            ["MCG_TRIAL_HARNESS_IDENTITY"] = state.Spec.Name,
            ["MCG_TRIAL_SOURCE_PROVENANCE"] = workload.SourceProvenance
        });
    }

    private static Mcg.AgentOrchestrator.Core.GoalTimingReportSnapshot? RedactHistoricalTiming(
        Mcg.AgentOrchestrator.Core.GoalTimingReportSnapshot? timing)
    {
        if (timing is null)
        {
            return null;
        }

        return timing with
        {
            Objective = string.Empty,
            Tasks = timing.Tasks.Select(task => task with
            {
                Description = string.Empty,
                Rounds = task.Rounds.Select(round => round with
                {
                    ValueEvidence = string.Empty,
                    WasteSource = string.Empty
                }).ToArray()
            }).ToArray(),
            CurrentHold = timing.CurrentHold is null
                ? null
                : timing.CurrentHold with { Blocker = string.Empty }
        };
    }

    private static void RunHarness(
        HarnessState state,
        ITrialRootSession session,
        TimeSpan timeout,
        List<string> failures)
    {
        try
        {
            var command = new ProcessStartInfo
            {
                FileName = state.Spec.FileName,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in state.Spec.Arguments)
            {
                command.ArgumentList.Add(argument);
            }

            using var launch = session.Start(command);
            var exited = launch.WaitForExit(ToTimeoutMilliseconds(timeout));
            if (exited)
            {
                state.ExitCode = launch.ExitCode;
            }

            try
            {
                state.StandardOutput = CaptureMetadata(launch.StdoutPath);
                state.StandardError = CaptureMetadata(launch.StderrPath);
                state.WorkerResult = InspectWorkerResult(launch.StdoutPath);
            }
            catch (Exception ex)
            {
                state.Outcome = TrialHarnessOutcome.ReceiptCaptureFailed;
                var failure = $"Harness '{state.Spec.Name}' receipt capture failed: {ex.Message}";
                state.Diagnostics.Add(failure);
                failures.Add(failure);
                return;
            }

            if (exited)
            {
                if (state.WorkerResult.Status == TrialWorkerResultStatus.Valid)
                {
                    state.Outcome = TrialHarnessOutcome.Completed;
                }
                else
                {
                    state.Outcome = TrialHarnessOutcome.WorkerResultInvalid;
                    var failure = $"Harness '{state.Spec.Name}' produced unusable WORKER_RESULT evidence ({state.WorkerResult.Status}).";
                    state.Diagnostics.Add(failure);
                    failures.Add(failure);
                }
            }
            else
            {
                state.Outcome = TrialHarnessOutcome.TimedOut;
                var failure = $"Harness '{state.Spec.Name}' timed out after {timeout}.";
                state.Diagnostics.Add(failure);
                failures.Add(failure);
            }
        }
        catch (Exception ex)
        {
            state.Outcome = TrialHarnessOutcome.LaunchFailed;
            state.Diagnostics.Add(ex.Message);
            failures.Add($"Harness '{state.Spec.Name}' launch failed: {ex.Message}");
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Launch timeout must be positive.");
        }

        return timeout.TotalMilliseconds >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(timeout.TotalMilliseconds);
    }

    private static TrialOutputMetadata CaptureMetadata(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Harness capture file does not exist: '{path}'.", path);
        }

        using var stream = File.OpenRead(path);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new TrialOutputMetadata(stream.Length, digest);
    }

    private static TrialWorkerResultEvidence InspectWorkerResult(string stdoutPath)
    {
        using var stream = File.OpenRead(stdoutPath);
        var truncated = stream.Length > MaximumWorkerResultInspectionBytes;
        var byteCount = (int)Math.Min(stream.Length, MaximumWorkerResultInspectionBytes);
        if (truncated)
        {
            stream.Seek(-byteCount, SeekOrigin.End);
        }

        var bytes = new byte[byteCount];
        stream.ReadExactly(bytes);
        var text = Encoding.UTF8.GetString(bytes);
        if (WorkerResultParser.TryParseFields(text, out var fields, out _))
        {
            return new TrialWorkerResultEvidence(TrialWorkerResultStatus.Valid, fields.Count);
        }

        if (truncated)
        {
            return new TrialWorkerResultEvidence(TrialWorkerResultStatus.InspectionLimitExceeded, 0);
        }

        var hasOpener = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Any(line => WorkerResultParser.IsOpener(line.Trim()));
        return new TrialWorkerResultEvidence(
            hasOpener ? TrialWorkerResultStatus.Malformed : TrialWorkerResultStatus.Missing,
            0);
    }

    private static HarnessReceiptPaths CreateHarnessReceiptPaths(string runDirectory, string harnessName)
    {
        var directory = Path.Combine(runDirectory, harnessName);
        Directory.CreateDirectory(directory);
        return new HarnessReceiptPaths(Path.Combine(directory, "result.json"));
    }

    private static void MarkTeardownUnclean(HarnessState state)
    {
        if (state.Outcome is TrialHarnessOutcome.NotAttempted or TrialHarnessOutcome.Completed)
        {
            state.Outcome = TrialHarnessOutcome.TeardownUnclean;
        }
    }

    private static TrialHarnessResult ToResult(HarnessState state) => new(
        state.Spec.Name,
        state.ResolvedBaseCommit,
        state.StandardOutput,
        state.StandardError,
        state.WorkerResult,
        state.ReceiptPath,
        state.TeardownReceiptPath,
        state.ExitCode,
        state.Outcome,
        state.Diagnostics.ToArray(),
        state.WorkloadIdentity,
        state.ArmIdentity,
        state.HistoricalTiming);

    private static void Validate(TrialComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceRepositoryPath))
        {
            throw new ArgumentException("A source repository path is required.", nameof(request));
        }

        if (!Directory.Exists(Path.GetFullPath(request.SourceRepositoryPath)))
        {
            throw new DirectoryNotFoundException($"Source repository does not exist: '{request.SourceRepositoryPath}'.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseCommit))
        {
            throw new ArgumentException("A base commit is required.", nameof(request));
        }

        if (request.Workload is null)
        {
            throw new ArgumentException("Canonical workload evidence is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Workload.BriefIdentity)
            || string.IsNullOrWhiteSpace(request.Workload.BriefContent)
            || string.IsNullOrWhiteSpace(request.Workload.BriefDigest)
            || string.IsNullOrWhiteSpace(request.Workload.ModelIdentity)
            || string.IsNullOrWhiteSpace(request.Workload.SourceProvenance))
        {
            throw new ArgumentException("Canonical workload fields cannot be empty.", nameof(request));
        }

        var actualDigest = TrialIdentity.ComputeBriefDigest(request.Workload.BriefContent);
        if (!actualDigest.Equals(request.Workload.BriefDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Canonical workload brief digest mismatch: expected '{request.Workload.BriefDigest}', actual '{actualDigest}'.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ReceiptsDirectory))
        {
            throw new ArgumentException("A receipts directory is required.", nameof(request));
        }

        if (request.Harnesses is null || request.Harnesses.Count < 2)
        {
            throw new ArgumentException("At least two harnesses are required.", nameof(request));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var harness in request.Harnesses)
        {
            if (string.IsNullOrWhiteSpace(harness.Name) ||
                harness.Name is "." or ".." ||
                harness.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException($"Harness name '{harness.Name}' is not a safe receipt-directory name.", nameof(request));
            }

            if (!names.Add(harness.Name))
            {
                throw new ArgumentException($"Harness name '{harness.Name}' is duplicated.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(harness.FileName))
            {
                throw new ArgumentException($"Harness '{harness.Name}' requires a fileName.", nameof(request));
            }

            if (harness.Environment?.Keys.Any(key => key.StartsWith(TrialIdentity.EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)) == true)
            {
                throw new ArgumentException(
                    $"Harness '{harness.Name}' cannot override reserved {TrialIdentity.EnvironmentPrefix} environment values.",
                    nameof(request));
            }
        }

        _ = ToTimeoutMilliseconds(request.LaunchTimeout ?? TimeSpan.FromMinutes(30));
    }

    private sealed class HarnessState(
        TrialHarnessSpec spec,
        HarnessReceiptPaths paths,
        Mcg.AgentOrchestrator.Core.GoalTimingReportSnapshot? historicalTiming)
    {
        public TrialHarnessSpec Spec { get; } = spec;
        public string ReceiptPath { get; } = paths.ReceiptPath;
        public string ResolvedBaseCommit { get; set; } = string.Empty;
        public string? TeardownReceiptPath { get; set; }
        public int? ExitCode { get; set; }
        public TrialHarnessOutcome Outcome { get; set; } = TrialHarnessOutcome.NotAttempted;
        public List<string> Diagnostics { get; } = [];
        public TrialWorkloadIdentity? WorkloadIdentity { get; set; }
        public TrialArmIdentity? ArmIdentity { get; set; }
        public Mcg.AgentOrchestrator.Core.GoalTimingReportSnapshot? HistoricalTiming { get; set; } = historicalTiming;
        public TrialOutputMetadata? StandardOutput { get; set; }
        public TrialOutputMetadata? StandardError { get; set; }
        public TrialWorkerResultEvidence WorkerResult { get; set; } = new(TrialWorkerResultStatus.NotInspected, 0);
    }

    private sealed record HarnessReceiptPaths(string ReceiptPath);
}
