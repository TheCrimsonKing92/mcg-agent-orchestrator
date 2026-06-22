// Console capture (ConsoleCapture.cs) is AsyncLocal-isolated and parallel-safe, BUT parallel
// collection execution exposed a hard test-host crash (dec62cc4): the worker-dispatch /
// process-spawning tests (GoalAcceptanceVerifier, WorkerDispatch, DispatchProcessHost, ChaosGate)
// concurrently spawn processes and write the shared config stores (workers.json / agents.json /
// model-functions.json), corrupting them mid-write and aborting the whole run ("host process exited
// unexpectedly") with no Passed! summary — which silently fails acceptance and blocks autonomous
// landing. Serial execution is validated clean (full-suite --blame run, exit 0). Restoring
// parallelism is a follow-up requiring per-test config-store + process isolation (a serial
// DisableParallelization collection for that cluster), not just the Console router.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
