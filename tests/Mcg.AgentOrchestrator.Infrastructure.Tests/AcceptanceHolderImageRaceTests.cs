using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique artifact roots and job names; all identity reads are injected.
public sealed class AcceptanceHolderImageRaceTests
{
    private const string Loader = @"C:\WINDOWS\SYSTEM32\ntdll.dll";
    private const string Executable = @"C:\Program Files\dotnet\dotnet.exe";

    [Fact]
    public void LeaseToleratesLoaderMetadataButRejectsRecycledOrDifferentResolvedHolder()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.Generation);
        using var claim = lease.Claim(fixture.Binding, fixture.Holder);
        Assert.Equal(fixture.Holder, lease.Read(fixture.Binding).Holder);

        lease.ValidateRecordedHolder(fixture.Binding, fixture.Holder with { ImagePath = Loader });
        Assert.Throws<InvalidDataException>(() => lease.ValidateRecordedHolder(fixture.Binding,
            fixture.Holder with { StartedAt = fixture.Holder.StartedAt.AddDays(1) }));
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
        Assert.Throws<InvalidDataException>(() => lease.ValidateRecordedHolder(fixture.Binding,
            fixture.Holder with { ImagePath = "other.exe" }));
        Assert.Throws<InvalidDataException>(() => lease.ValidateRecordedHolder(fixture.Binding,
            fixture.Holder with { ProcessId = fixture.Holder.ProcessId + 1, ImagePath = Loader }));
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
    }

    [Theory]
    [InlineData(Loader, Executable, 0, 0, "LiveMatch")]
    [InlineData(Executable, Loader, 0, 0, "LiveMatch")]
    [InlineData(Loader, Executable, 0, 1, "DeadOrRecycled")]
    [InlineData(Executable, "other.exe", 0, 0, "Unknown")]
    [InlineData(Loader, Executable, 1, 0, "Unknown")]
    [InlineData("", Executable, 0, 0, "LiveMatch")]
    [InlineData("C:/Program Files/dotnet/dotnet.exe", Executable, 0, 0, "LiveMatch")]
    public void RecordedIdentityClassifiesInjectedImages(
        string recordedImage, string observedImage, int pidDelta, int startShiftDays, string expected)
    {
        var recorded = new SpawnProcessIdentity(222, DateTimeOffset.UnixEpoch, recordedImage);
        var observed = new SpawnProcessIdentity(222 + pidDelta,
            DateTimeOffset.UnixEpoch.AddDays(startShiftDays), observedImage);

        var status = SpawnProcessIdentityReader.EvaluateRecordedIdentity(recorded, observed, out _);

        Assert.Equal(expected, status.ToString());
    }

    [Theory]
    [InlineData(Loader, 0, 0, true)]
    [InlineData(Loader, 0, 1, false)]
    [InlineData(Loader, 1, 0, false)]
    [InlineData("other.exe", 0, 0, false)]
    public void AbsentJobSettlementToleratesOnlyUnresolvedImageDifferences(
        string receiptImage, int pidDelta, int startShiftDays, bool expectedSettled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var name = @"Local\acceptance-absent-" + Guid.NewGuid().ToString("N");
        var receiptHolder = fixture.Holder with
        {
            ImagePath = receiptImage,
            ProcessId = fixture.Holder.ProcessId + pidDelta,
            StartedAt = fixture.Holder.StartedAt.AddDays(startShiftDays)
        };
        File.WriteAllText(fixture.Binding.SettlementPath,
            JsonSerializer.Serialize(new AcceptanceJobSettlement(name, receiptHolder, true)));

        var observation = DurableAcceptanceJob.Observe(name, fixture.Holder,
            SpawnTrackedProcessStatus.DeadOrRecycled, cancel: false, fixture.Binding.SettlementPath);

        Assert.Equal(expectedSettled, observation.Settled);
        if (expectedSettled) Assert.Equal("job-settlement-receipt", observation.Detail);
    }

    private sealed class Fixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "holder-image-race-" + Guid.NewGuid().ToString("N"));
        internal AcceptanceHolderBinding Binding { get; }
        internal SpawnProcessIdentity Generation { get; } = new(111, DateTimeOffset.UnixEpoch, "conductor.exe");
        internal SpawnProcessIdentity Holder { get; } = new(222, DateTimeOffset.UnixEpoch, Executable);

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Binding = new("attempt", Path.Combine(Root, "attempt.attempt.json"), "candidate:branch:main", "slot-0");
        }

        internal AcceptanceHolderLease Lease() => new(
            utcNow: () => DateTimeOffset.UnixEpoch,
            classify: identity => identity == Holder || identity == Generation
                ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
            attach: name =>
            {
                Assert.Equal(Binding.JobName, name);
                return new EmptyDisposable();
            },
            job: (name, holder, status, _) =>
            {
                Assert.Equal(Binding.JobName, name);
                Assert.Equal(Holder, holder);
                return new(status == SpawnTrackedProcessStatus.LiveMatch, false, "active");
            },
            stopUnclaimed: _ => true);

        public void Dispose() => Directory.Delete(Root, true);
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
}
