# Worker build authority negative control

Acceptance owns execution and receipt capture. Use an isolated candidate; do not mutate the operator's active host.

1. Build the normal candidate and execute `WorkerBuildRequestAuthorityTests.WrongOriginCannotAuthorizeHost`. Require positive discovery/execution counts and passing normal code.
2. In `WorkerBuildInvocationOperations.RequireOrigin`, remove only the clause `BuildWorkerDispatchId(goal.Id, task.Id, dispatch) != reference.DispatchId ||`, including its trailing `||`. Keep every other binding check and the test unchanged.
3. Rebuild and execute the identical selection. The `dispatch` theory row must fail at the exception assertion because the forged dispatch now reaches host construction. A compile/setup failure, zero executions or a passing assertion is not a valid negative control.
4. Restore production source, rebuild and rerun that selection. Require restored green execution. Retain the mutation diff, exact candidate/configuration, selected/executed counts, red assertion and restored result in the normal Acceptance evidence package.

The foreign-peer control independently requires an actual connected OS PID to be rejected before the service callback. No worker-authored JSON or copied timeout text supplies phase evidence in either control.
