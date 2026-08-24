using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class TrialHarnessComparison(ITrialRootHost host)
{
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

        var states = request.Harnesses
            .Select(spec => new HarnessState(spec, CreateHarnessReceiptPaths(runDirectory, spec.Name)))
            .ToArray();
        var created = new List<(HarnessState State, ITrialRootSession Session)>();
        var failures = new List<string>();

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
                    foreach (var item in created)
                    {
                        RunHarness(item.State, item.Session, request.LaunchTimeout ?? TimeSpan.FromMinutes(30), failures);
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
                        state.Outcome = TrialHarnessOutcome.TeardownUnclean;
                        var failure = $"Harness '{state.Spec.Name}' trial-root teardown was unclean: {string.Join("; ", report.Diagnostics)}";
                        state.Diagnostics.Add(failure);
                        failures.Add(failure);
                    }
                }
                catch (Exception ex)
                {
                    state.Outcome = TrialHarnessOutcome.TeardownUnclean;
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
                        state.Outcome = TrialHarnessOutcome.TeardownUnclean;
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
            failures.Count == 0);
        File.WriteAllText(comparisonReceiptPath, JsonSerializer.Serialize(comparison, ReceiptJson));
        return comparison;
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
                CreateNoWindow = true,
                WorkingDirectory = session.RootPath
            };
            foreach (var argument in state.Spec.Arguments)
            {
                command.ArgumentList.Add(argument);
            }

            using var launch = session.Start(command);
            var exited = launch.WaitForExit(ToTimeoutMilliseconds(timeout));
            CopyReceipt(launch.StdoutPath, state.StdoutPath);
            CopyReceipt(launch.StderrPath, state.StderrPath);
            if (exited)
            {
                state.ExitCode = launch.ExitCode;
                state.Outcome = TrialHarnessOutcome.Completed;
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

    private static void CopyReceipt(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static HarnessReceiptPaths CreateHarnessReceiptPaths(string runDirectory, string harnessName)
    {
        var directory = Path.Combine(runDirectory, harnessName);
        Directory.CreateDirectory(directory);
        var stdout = Path.Combine(directory, "stdout.log");
        var stderr = Path.Combine(directory, "stderr.log");
        File.WriteAllText(stdout, string.Empty);
        File.WriteAllText(stderr, string.Empty);
        return new HarnessReceiptPaths(stdout, stderr, Path.Combine(directory, "result.json"));
    }

    private static TrialHarnessResult ToResult(HarnessState state) => new(
        state.Spec.Name,
        state.ResolvedBaseCommit,
        state.StdoutPath,
        state.StderrPath,
        state.ReceiptPath,
        state.TeardownReceiptPath,
        state.ExitCode,
        state.Outcome,
        state.Diagnostics.ToArray());

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
        }

        _ = ToTimeoutMilliseconds(request.LaunchTimeout ?? TimeSpan.FromMinutes(30));
    }

    private sealed class HarnessState(TrialHarnessSpec spec, HarnessReceiptPaths paths)
    {
        public TrialHarnessSpec Spec { get; } = spec;
        public string StdoutPath { get; } = paths.StdoutPath;
        public string StderrPath { get; } = paths.StderrPath;
        public string ReceiptPath { get; } = paths.ReceiptPath;
        public string ResolvedBaseCommit { get; set; } = string.Empty;
        public string? TeardownReceiptPath { get; set; }
        public int? ExitCode { get; set; }
        public TrialHarnessOutcome Outcome { get; set; } = TrialHarnessOutcome.LaunchFailed;
        public List<string> Diagnostics { get; } = [];
    }

    private sealed record HarnessReceiptPaths(string StdoutPath, string StderrPath, string ReceiptPath);
}
