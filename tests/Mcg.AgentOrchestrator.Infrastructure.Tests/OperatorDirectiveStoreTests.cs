using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns a workspace and non-pooled database; all clocks are fixed.
public sealed class OperatorDirectiveStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Id = "aaaaaaaa111111111111111111111111";

    [Fact]
    public void RoundTrip_PreservesAttributionAndExpiry_AndReplaysOnce()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        var expected = Directive(Id, At.AddHours(1));
        Assert.True(store.TryAppendDirective(expected, "record"));
        Assert.False(store.TryAppendDirective(expected with { Id = "different" }, "record"));
        Assert.Equal(expected, Assert.Single(store.List(At)));
        Assert.True(store.HasRecordSource("record"));
        Assert.Throws<InvalidOperationException>(() => store.TryAppendDirective(expected, "other"));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Expiry_ComputedAtReadTime_IncludesEquality(int hours, bool expired)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        store.TryAppendDirective(Directive(Id, At.AddHours(hours)), "source");
        Assert.Equal(expired, Assert.Single(store.List(At)).Expired);
        Assert.True(Assert.Single(store.List(At.AddHours(2))).Expired);
        Assert.False(Assert.Single(store.List(At.AddHours(-2))).Expired);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void UntilHandoff_RequiresStrictlyLaterMarker(int minutes, bool expired)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        store.TryAppendDirective(Directive(Id) with { UntilHandoff = true }, "record");
        Assert.False(Assert.Single(store.List(At)).Expired);
        Assert.True(store.TryAppendHandoff("handoff-id", "handoff-source", "new owner", "owner",
            OperatorActorKind.Human, "cli", At.AddMinutes(minutes)));
        Assert.False(store.TryAppendHandoff("different-id", "handoff-source", null, "owner",
            OperatorActorKind.Human, "cli", At.AddMinutes(5)));
        Assert.True(store.HasHandoffSource("handoff-source"));
        Assert.Equal(expired, Assert.Single(store.List(At)).Expired);
    }

    [Fact]
    public void Retirement_DistinguishesUnknownRetiredAndReplay_AndKeepsAudit()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        Assert.Equal((false, false, "directive-not-found"), Retire(store, "bbbbbbbb", "unknown"));
        Assert.False(File.Exists(fixture.Workspace.OperatorDirectivesStorePath));
        store.TryAppendDirective(Directive(Id, At.AddHours(-1)), "record");
        Assert.Equal((false, false, "directive-not-found"), Retire(store, "bbbbbbbb", "unknown"));
        Assert.Equal((true, false, (string?)null), Retire(store, Id[..8], "retire"));
        Assert.Empty(store.List(At));
        var retired = Assert.Single(store.List(At, includeRetired: true));
        Assert.Equal("superseded", retired.RetireReason);
        Assert.Equal("new-owner", retired.RetiredBy);
        Assert.Equal(At.AddMinutes(1), retired.RetiredAt);
        Assert.Equal((false, true, (string?)null), Retire(store, "wrong-id", "retire"));
        Assert.Equal((false, false, "directive-already-retired"), Retire(store, Id, "second"));
        Assert.True(store.HasRetirementSource("retire"));
        Assert.False(store.HasRetirementSource("second"));
    }

    [Fact]
    public void Prefixes_RejectAmbiguousAndShort_AndResolveUniqueCaseInsensitively()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var store = new SqliteOperatorDirectiveStore(fixture.Workspace.OperatorDirectivesStorePath);
        store.TryAppendDirective(Directive(Id), "one");
        store.TryAppendDirective(Directive("aaaaaaaa222222222222222222222222"), "two");
        Assert.Equal("directive-id-prefix-too-short", Retire(store, "aaaa", "short").Rejection);
        Assert.Equal("directive-id-ambiguous", Retire(store, "aaaaaaaa", "ambiguous").Rejection);
        Assert.Equal(2, store.List(At).Count);
        Assert.True(Retire(store, "AAAAAAAA1", "unique").Appended);
        Assert.Equal("aaaaaaaa222222222222222222222222", Assert.Single(store.List(At)).Id);
    }

    [Fact]
    public void ReadOnlyStore_PreservesBytesAndRejectsAllWrites()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.OperatorDirectivesStorePath;
        new SqliteOperatorDirectiveStore(path).TryAppendDirective(Directive(Id), "record");
        var before = File.ReadAllBytes(path);
        var store = SqliteOperatorDirectiveStore.OpenReadOnly(path);
        Assert.Equal(Directive(Id), Assert.Single(store.List(At)));
        Assert.Throws<InvalidOperationException>(() => store.TryAppendDirective(Directive("new"), "new"));
        Assert.Throws<InvalidOperationException>(() => Retire(store, Id, "retire"));
        Assert.Throws<InvalidOperationException>(() => store.TryAppendHandoff("handoff", "source", null,
            "owner", OperatorActorKind.Human, "cli", At));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static OperatorDirective Directive(string id, DateTimeOffset? expires = null) =>
        new(id, "pacing", "One goal at a time", "owner", OperatorActorKind.Human, "cli", At,
            expires, false, null, null, null, false);

    private static (bool Appended, bool Replayed, string? Rejection) Retire(
        SqliteOperatorDirectiveStore store, string id, string source) =>
        store.TryAppendRetirement(id, source, "superseded", "new-owner", OperatorActorKind.Human,
            "cli", At.AddMinutes(1));
}
