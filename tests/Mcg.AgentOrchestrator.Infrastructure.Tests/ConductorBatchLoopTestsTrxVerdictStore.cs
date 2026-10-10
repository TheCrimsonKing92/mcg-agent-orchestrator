using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTrxVerdictStore : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsTrxVerdictStore(ITestOutputHelper output) : base(output)
    {
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_LoadsVerdictsOnceBeforeProgressAndFirstTick(bool withWorkspace)
    {
        var root = CreateTempDirectory("mcg-trx-verdict-loop");
        try
        {
            var workspace = withWorkspace ? OrchestratorWorkspace.ForDirectory(root) : null;
            var stopPath = Path.Combine(root, "stop");
            var expectedDirectory = workspace?.OrchestratorDirectory ?? root;
            var events = new List<string>();
            var directories = new List<string>();
            var loop = new ConductorBatchLoop(
                workspace: workspace,
                loadTrxCoherenceVerdicts: directory =>
                {
                    directories.Add(directory);
                    events.Add("load");
                    Console.WriteLine("TRX_STORE_LOAD");
                });

            for (var start = 0; start < 2; start++)
            {
                events.Clear();
                var (kernel, _) = SimpleGoal();
                var output = CaptureConsole(() => loop.Run(
                    kernel,
                    MakeDriver(),
                    ConductorAutonomyPolicy.Conservative,
                    stopPath,
                    maxIterations: 1,
                    sleepFunc: _ => true,
                    onTick: _ =>
                    {
                        events.Add("tick");
                        Console.WriteLine("TRX_STORE_TICK");
                    }));

                Assert.Equal(new[] { "load", "tick" }, events);
                Assert.Equal(start + 1, directories.Count);
                Assert.All(directories, directory => Assert.Equal(expectedDirectory, directory));
                Assert.Single(output.Split('\n').Where(line => line.StartsWith("LOOP_START policy=", StringComparison.Ordinal)));
                var loadIndex = output.IndexOf("TRX_STORE_LOAD", StringComparison.Ordinal);
                var progressIndex = output.IndexOf("LOOP_START policy=", StringComparison.Ordinal);
                var tickIndex = output.IndexOf("TRX_STORE_TICK", StringComparison.Ordinal);
                Assert.True(loadIndex < progressIndex && progressIndex < tickIndex);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
