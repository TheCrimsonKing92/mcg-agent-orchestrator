using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PipeDrainSaturationProbeChildTests
{
    internal const string ModeVariable = "MCG_PIPE_DRAIN_SATURATION_PROBE_MODE";
    internal const string ReceiptPathVariable = "MCG_PIPE_DRAIN_SATURATION_PROBE_RECEIPT";
    internal const string DedicatedReaderMode = "dedicated-reader";
    internal const string PoolDependentReaderMode = "pool-dependent-reader";

    [Xunit.Fact]
    public void RunProbeWhenExplicitlyLaunched()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        var receiptPath = Environment.GetEnvironmentVariable(ReceiptPathVariable);
        if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(receiptPath))
        {
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunProbe(mode, receiptPath);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = false,
            Name = "pipe-drain-saturation-probe"
        };

        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RunProbe(string mode, string receiptPath)
    {
        const string expected = "complete-pipe-output";
        Task<string>? poolDependentRead = null;
        Exception? failure = null;
        string? failureKind = null;
        BoundedSaturationEvidence? evidence = null;
        var poolDependentReadCompleted = false;

        try
        {
            BoundedThreadPoolSaturationApparatus.RunWithBoundedSaturatedThreadPool(currentEvidence =>
            {
                evidence = currentEvidence;
                poolDependentRead = new PipeDrainThreadPoolSaturationTests.SynchronousOnlyTextReader(expected)
                    .ReadToEndAsync();
                poolDependentReadCompleted = poolDependentRead.IsCompleted;

                if (mode == DedicatedReaderMode)
                {
                    var drain = PipeDrain.Start(
                        new PipeDrainThreadPoolSaturationTests.SynchronousOnlyTextReader(expected),
                        "pipe-drain-saturation-probe-reader");
                    if (!drain.Join(Environment.TickCount64 + 2_000))
                    {
                        failureKind = "dedicated-reader-timeout";
                        throw new Xunit.Sdk.XunitException(PipeDrain.DescribeTimeout("probe", 2_000, drain, null));
                    }

                    Xunit.Assert.Equal(expected, drain.Text);
                    if (poolDependentReadCompleted)
                    {
                        failureKind = "negative-control-unsaturated";
                        throw new Xunit.Sdk.XunitException("Negative control unexpectedly found a free thread-pool worker.");
                    }

                    return;
                }

                if (mode == PoolDependentReaderMode)
                {
                    if (!poolDependentReadCompleted)
                    {
                        failureKind = "dedicated-reader-substitution-starved";
                        throw new Xunit.Sdk.XunitException(
                            "Dedicated-reader substitution remained starved as required by the negative control.");
                    }

                    failureKind = "dedicated-reader-substitution-completed";
                    throw new Xunit.Sdk.XunitException(
                        "Dedicated-reader substitution unexpectedly completed while no worker should be available.");
                }

                failureKind = "unknown-mode";
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported saturation probe mode.");
            });
        }
        catch (Exception exception)
        {
            failure = exception;
            failureKind ??= "apparatus-or-probe-failure";
        }
        finally
        {
            if (poolDependentRead is not null)
            {
                poolDependentRead.GetAwaiter().GetResult();
            }

            var receipt = new PipeDrainSaturationProbeReceipt(
                mode,
                Environment.ProcessId,
                evidence?.WorkerBound ?? 0,
                evidence?.BarrierCount ?? 0,
                evidence?.ThreadCountBefore ?? ThreadPool.ThreadCount,
                ThreadPool.ThreadCount,
                evidence?.AvailableWorkersAfterBarrier ?? -1,
                poolDependentReadCompleted,
                failure is null ? "passed" : "failed",
                failureKind);
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt));
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

internal sealed record PipeDrainSaturationProbeReceipt(
    string Mode,
    int ProcessId,
    int WorkerBound,
    int BarrierCount,
    int ThreadCountBefore,
    int ThreadCountAfter,
    int AvailableWorkersAfterBarrier,
    bool PoolDependentReadCompleted,
    string Outcome,
    string? FailureKind);
