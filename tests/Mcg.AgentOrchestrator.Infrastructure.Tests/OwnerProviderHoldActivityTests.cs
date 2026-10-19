using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure event inputs and fixed instants.
public sealed class OwnerProviderHoldActivityTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-17T11:08:00Z");
    private const string Goal = "11111111222233334444555566667777";
    private const string Due = "2026-10-17T11:38:00.0000000+00:00";

    [Theory]
    [InlineData("provider-hold-placed", "Provider paused: xai has no usage balance left",
        "The provider reported that its usage balance is exhausted; next check " + Due + " (attempt 1).",
        "Work assigned to xai waits; the conductor tries one task at the next check.",
        "No. If the provider states a reset time, record it with provider-reset.")]
    [InlineData("provider-hold-probe", "Provider check: trying one task on xai",
        "The scheduled check for xai is due (attempt 1).",
        "If the task runs, the hold clears; otherwise the next check is scheduled.",
        "No. You can let the conductor continue.")]
    [InlineData("provider-hold-restored", "Provider back: xai is accepting work again",
        "A task on xai ran after the hold, so the hold has cleared.",
        "Work waiting for xai continues.", "No. You can let the conductor continue.")]
    public void Narrate_HoldTransition_UsesExactOwnerTexts(string kind, string sentence,
        string reason, string next, string act)
    {
        var evt = Event(kind);
        Assert.True(OwnerActivityNarrator.Maps(evt));
        var row = Assert.Single(OwnerActivityNarrator.Narrate([evt], _ => ""));
        Assert.Equal(sentence, row.Phrase);
        Assert.Equal(reason, row.Why);
        Assert.Equal(next, row.Next);
        Assert.Equal(act, row.Act);
    }

    [Theory]
    [InlineData(@"claude::c:\users\fixture\.claude-2")]
    [InlineData(@"claude::c:\users\fixture user\.claude-2")]
    [InlineData("claude::/users/fixture user/.claude-2")]
    public void Narrate_CredentialBinding_PreservesPathAndShowsLeaf(string binding)
    {
        var evt = Event("provider-hold-placed", binding);
        var row = Assert.Single(OwnerActivityNarrator.Narrate([evt], _ => ""));
        Assert.Equal("Provider paused: claude (.claude-2) has no usage balance left", row.Phrase);
        Assert.Equal(binding, OwnerProviderHoldNarration.Binding(evt));
        Assert.StartsWith($"{At.ToLocalTime():HH:mm:ss} {Goal[..8]} ", OwnerActivityNarrator.Line(row));
    }

    [Fact]
    public void Narrate_UnavailableSchedule_ShowsUnavailableReason()
    {
        var evt = Event("provider-hold-placed") with { Detail = "PROVIDER_HOLD_PLACED next_probe=unavailable attempt=0 binding=xai::<provider-default>" };
        var row = Assert.Single(OwnerActivityNarrator.Narrate([evt], _ => ""));
        Assert.Equal("The provider reported that its usage balance is exhausted; the next check time is unavailable.", row.Why);
    }

    [Theory]
    [InlineData("provider-hold-placed")]
    [InlineData("provider-hold-probe")]
    [InlineData("provider-hold-restored")]
    public void Narrate_MissingOrEmptyBinding_EmitsNoRow(string kind)
    {
        Assert.Empty(OwnerActivityNarrator.Narrate([new(At, kind, Goal, "PROVIDER_HOLD source=11111111/22222222")], _ => ""));
        Assert.Empty(OwnerActivityNarrator.Narrate([new(At, kind, Goal, "PROVIDER_HOLD binding=  ")], _ => ""));
    }

    private static OwnerConductEvent Event(string kind, string binding = "xai::<provider-default>") =>
        new(At, kind, Goal, $"PROVIDER_HOLD source=11111111/22222222 next_probe={Due} attempt=1 binding={binding}");
}
