# Behavioral RED/GREEN specification for fixed-wait removal

Criterion 1 was amended after review to split evidence by capability. This file is the Developer-owned
mutation specification. The RED and GREEN arms have **not been executed**. The operator owns applying
the mutation, running both arms in a test-capable lane, recording the receipts and counts, and reverting
the mutation. No execution result is reconstructed or estimated here.

## Disjoint goal transactions remain independent

- Fully qualified test identity:
  `SqliteOrchestratorStateRepositoryTests.TransactGoalAsync_DisjointGoalsDoNotBlockEachOther`
- Display name:
  `SqliteOrchestratorStateRepository_TransactGoalAsync_disjoint_goals_do_not_block_each_other`
- Test source: `tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SqliteOrchestratorStateRepositoryTests.cs:2487`
- Mutation target: `src/Mcg.AgentOrchestrator.Infrastructure/Persistence/SqliteOrchestratorStateRepository.cs:2077`
- Exercised path: the test calls the public `TransactGoalAsync<T>(GoalId, ...)` overload at line 2051 for
  both goals. That overload delegates directly to the operation-named overload at line 2063, whose
  load-mutate-CAS loop invokes the supplied transaction delegate at line 2078. Apply the mutation there,
  not to the neighbouring `TransactGoalStateAsync<T>` path.

Apply this exact mutation against the current source:

```diff
@@
             // Delegate produces the new snapshot to write (runs outside the write lock).
-            var (shouldSave, newSnapshot, result) = await transaction(loadedSnapshot, cancellationToken);
+            var heldWrite = await BeginWriteAsync($"{resolvedOperation}.negative-control", cancellationToken);
+            bool shouldSave;
+            GoalSnapshot? newSnapshot;
+            T result;
+            await using (var heldConnection = heldWrite.Connection)
+            {
+                (shouldSave, newSnapshot, result) = await transaction(loadedSnapshot, cancellationToken);
+                await RunNonQueryAsync(heldConnection, "ROLLBACK", cancellationToken);
+                heldWrite.Telemetry.Emit("rollback");
+            }

             if (!shouldSave || newSnapshot is null)
```

This mutation deliberately holds `BEGIN IMMEDIATE` while goal A's delegate waits on
`releaseMutateA`. Goal B reaches the same confirmed `TransactGoalAsync<T>` loop and cannot acquire its
write transaction until goal A is released.

Expected RED output: the test fails at
`await taskB.WaitAsync(TimeSpan.FromSeconds(5))` with
`System.TimeoutException: The operation has timed out.` The failure precedes `Assert.False`, so no
xUnit assertion message is expected. The test's `finally` block releases goal A, allowing cleanup to
finish.

GREEN procedure: revert only the diff above and run the identical test selection. The test should pass
because goal B completes while goal A's delegate is still held. RED and GREEN execution, discovered
counts, and receipts remain operator-owned and are intentionally absent from this Developer artifact.
