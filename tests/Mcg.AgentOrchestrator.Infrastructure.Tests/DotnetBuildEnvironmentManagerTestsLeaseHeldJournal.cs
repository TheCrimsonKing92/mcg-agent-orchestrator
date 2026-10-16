using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildEnvironmentManagerStaticHooks)]
public sealed class DotnetBuildEnvironmentManagerTestsLeaseHeldJournal : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Fact]
    public void Release_WitnessesCompileInSeparateJournal_AndFreesSlot()
    {
        var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            TimeSpan.Zero, slotCount: 1, storageRoot: StorageRoot,
            holderLabel: "pre-review-evidence:goal-abcd1234:attempt-x1");
        var environment = lease.Environment;
        using (lease)
        {
            var file = Path.Combine(environment.ArtifactsPath!, "obj", "Proj", "debug", "Proj.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "assembly");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        }
        var folder = Path.GetDirectoryName(environment.ExecutionLockPath)!;
        using var entry = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Path.Combine(folder, BuildSlotHoldRecord.JournalFileName))));
        var r = entry.RootElement;
        Assert.Equal("LEASE_HELD", r.GetProperty("event").GetString());
        Assert.Equal("released", r.GetProperty("decision").GetString());
        Assert.Equal(Path.GetFileNameWithoutExtension(environment.ExecutionLockPath), r.GetProperty("slot").GetString());
        Assert.Equal(environment.LeaseId, r.GetProperty("leaseId").GetString());
        Assert.Equal(environment.ArtifactsPath, r.GetProperty("triggerPath").GetString());
        var reason = r.GetProperty("reason").GetString()!;
        Assert.Contains("holder=pre-review-evidence:goal-abcd1234:attempt-x1", reason);
        Assert.Matches(@"heldMs=\d+", reason);
        Assert.Contains("compiled=Proj/debug@", reason);
        var oldJournal = Path.Combine(folder, "lease.journal.jsonl");
        Assert.True(!File.Exists(oldJournal) || !File.ReadAllText(oldJournal).Contains("LEASE_HELD", StringComparison.Ordinal));
        using var next = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            TimeSpan.Zero, slotCount: 1, storageRoot: StorageRoot);
        Assert.Equal(environment.ExecutionLockPath, next.Environment.ExecutionLockPath);
    }

    [Fact]
    public void Release_WithoutLabelOrObj_RecordsUnknownWitness()
    {
        var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            TimeSpan.Zero, slotCount: 1, storageRoot: StorageRoot);
        var folder = Path.GetDirectoryName(lease.Environment.ExecutionLockPath)!;
        lease.Dispose();
        using var entry = JsonDocument.Parse(Assert.Single(File.ReadAllLines(Path.Combine(folder, BuildSlotHoldRecord.JournalFileName))));
        var reason = entry.RootElement.GetProperty("reason").GetString()!;
        Assert.StartsWith("holder=unlabeled ", reason);
        Assert.EndsWith("compiled=unknown", reason);
    }
}
