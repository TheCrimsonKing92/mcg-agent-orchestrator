using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Serialized: this test deliberately occupies the thread pool while the dedicated reader runs.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class PipeDrainThreadPoolSaturationTests
{
    [Xunit.Fact(DisplayName = "PipeDrain_reads_to_end_while_pool_dependent_async_read_is_starved")]
    public void PipeDrainReadsToEndWhilePoolDependentAsyncReadIsStarved()
    {
        var result = RunSaturationProbe(PipeDrainSaturationProbeChildTests.DedicatedReaderMode);

        Assert.True(result.ExitCode == 0, result.DescribeFailure());
        Assert.Equal("passed", result.Receipt.Outcome);
        Assert.Equal(PipeDrainSaturationProbeChildTests.DedicatedReaderMode, result.Receipt.Mode);
        Assert.Equal(0, result.Receipt.AvailableWorkersAfterBarrier);
        Assert.False(result.Receipt.PoolDependentReadCompleted, "Negative control unexpectedly found a free thread-pool worker.");
    }

    [Xunit.Fact(DisplayName = "PipeDrain_reads_to_end_while_pool_dependent_async_read_is_starved_after_parent_pool_growth")]
    public void PipeDrainReadsToEndWhilePoolDependentAsyncReadIsStarvedAfterParentPoolGrowth()
    {
        var before = ThreadPool.ThreadCount;
        RunWithSaturatedThreadPool(static () => { });
        var after = ThreadPool.ThreadCount;
        Assert.True(after > before, $"Parent pool did not grow: before={before}; after={after}.");

        var result = RunSaturationProbe(PipeDrainSaturationProbeChildTests.DedicatedReaderMode);

        Assert.True(result.ExitCode == 0, result.DescribeFailure());
        Assert.Equal("passed", result.Receipt.Outcome);
        Assert.Equal(0, result.Receipt.AvailableWorkersAfterBarrier);
    }

    [Xunit.Fact(DisplayName = "PipeDrain_saturation_probe_fails_when_dedicated_reader_is_replaced_by_pool_dependent_reading")]
    public void PipeDrainSaturationProbeFailsWhenDedicatedReaderIsReplacedByPoolDependentReading()
    {
        var result = RunSaturationProbe(PipeDrainSaturationProbeChildTests.PoolDependentReaderMode);

        Assert.True(result.ExitCode != 0, result.DescribeFailure());
        Assert.Equal("failed", result.Receipt.Outcome);
        Assert.Equal("dedicated-reader-substitution-starved", result.Receipt.FailureKind);
    }

    private static SaturationProbeProcessResult RunSaturationProbe(string mode)
    {
        var root = CreateTempDirectory();
        var receiptPath = Path.Combine(root, "receipt.json");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveDotnetHostPath(),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.Environment[PipeDrainSaturationProbeChildTests.ModeVariable] = mode;
        process.StartInfo.Environment[PipeDrainSaturationProbeChildTests.ReceiptPathVariable] = receiptPath;
        process.StartInfo.ArgumentList.Add(typeof(PipeDrainSaturationProbeChildTests).Assembly.Location);
        process.StartInfo.ArgumentList.Add("--filter-class");
        process.StartInfo.ArgumentList.Add("*PipeDrainSaturationProbeChildTests*");

        try
        {
            Assert.True(process.Start(), "Failed to start the managed pipe-drain saturation probe.");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(35_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new Xunit.Sdk.XunitException(
                    $"Pipe-drain saturation probe timed out. stdout:{Environment.NewLine}{stdout.GetAwaiter().GetResult()}" +
                    $"{Environment.NewLine}stderr:{Environment.NewLine}{stderr.GetAwaiter().GetResult()}");
            }

            var completedStdout = stdout.GetAwaiter().GetResult();
            var completedStderr = stderr.GetAwaiter().GetResult();
            var result = new SaturationProbeProcessResult(
                process.ExitCode,
                completedStdout,
                completedStderr,
                ReadSaturationProbeReceipt(receiptPath, completedStdout, completedStderr));
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // The child receipt and process result are the test evidence; cleanup remains best effort.
            }
        }
    }

    private static PipeDrainSaturationProbeReceipt ReadSaturationProbeReceipt(
        string receiptPath,
        string stdout,
        string stderr)
    {
        if (!File.Exists(receiptPath))
        {
            throw new Xunit.Sdk.XunitException(
                $"Pipe-drain saturation probe did not write receipt '{receiptPath}'." +
                $"{Environment.NewLine}stdout:{Environment.NewLine}{stdout}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }

        try
        {
            return JsonSerializer.Deserialize<PipeDrainSaturationProbeReceipt>(File.ReadAllText(receiptPath))
                ?? throw new Xunit.Sdk.XunitException($"Pipe-drain saturation probe receipt '{receiptPath}' was empty.");
        }
        catch (JsonException exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"Pipe-drain saturation probe receipt '{receiptPath}' was malformed: {exception.Message}" +
                $"{Environment.NewLine}stdout:{Environment.NewLine}{stdout}" +
                $"{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        }
    }

    private sealed record SaturationProbeProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        PipeDrainSaturationProbeReceipt Receipt)
    {
        internal string DescribeFailure() =>
            $"Pipe-drain saturation probe exited {ExitCode}." +
            $"{Environment.NewLine}stdout:{Environment.NewLine}{Stdout}" +
            $"{Environment.NewLine}stderr:{Environment.NewLine}{Stderr}";
    }

    internal static void RunWithSaturatedThreadPool(Action action)
    {
        var release = new ManualResetEventSlim(false);
        ThreadPool.GetMinThreads(out var minWorkerThreads, out _);
        var blockedItems = Math.Max(minWorkerThreads, Environment.ProcessorCount) * 2 + 32;
        var callbacksCompleted = new CountdownEvent(blockedItems);
        try
        {
            for (var i = 0; i < blockedItems; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static state =>
                    {
                        var (releaseSignal, completionSignal) =
                            ((ManualResetEventSlim, CountdownEvent))state!;
                        try
                        {
                            releaseSignal.Wait();
                        }
                        finally
                        {
                            completionSignal.Signal();
                        }
                    },
                    (release, callbacksCompleted));
            }

            Thread.Sleep(250);
            action();
        }
        finally
        {
            release.Set();
            if (!callbacksCompleted.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    $"Thread-pool saturation callbacks did not complete after release; " +
                    $"remainingCallbacks={callbacksCompleted.CurrentCount}; " +
                    $"poolPendingWorkItems={ThreadPool.PendingWorkItemCount}.");
            }

            callbacksCompleted.Dispose();
            release.Dispose();
        }
    }

    internal sealed class SynchronousOnlyTextReader(string text) : TextReader
    {
        private int _position;

        public override int Read(char[] buffer, int index, int count)
        {
            var remaining = text.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var copied = Math.Min(count, remaining);
            text.CopyTo(_position, buffer, index, copied);
            _position += copied;
            return copied;
        }
    }
}
