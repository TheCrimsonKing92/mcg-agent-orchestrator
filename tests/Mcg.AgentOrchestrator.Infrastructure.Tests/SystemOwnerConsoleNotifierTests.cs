using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: sound and file existence use local recording fakes; no native calls.
public sealed class SystemOwnerConsoleNotifierTests
{
    [Fact]
    public void DefaultMappingUsesTheThreeSystemAliases()
    {
        var output = new SoundOutput();
        var notifier = new SystemOwnerConsoleNotifier(output, new(), _ => throw new InvalidOperationException("no file configured"));
        notifier.Notify(OwnerConsoleNotificationTier.Calm);
        notifier.Notify(OwnerConsoleNotificationTier.Attention);
        notifier.Notify(OwnerConsoleNotificationTier.Alarm);
        Assert.Equal([0x40u, 0x30u, 0x10u], output.SystemSounds);
        Assert.Empty(output.Files);
    }

    [Theory]
    [InlineData(0, 0x40u)]
    [InlineData(1, 0x30u)]
    [InlineData(2, 0x10u)]
    public void ExistingFileOverridesSoundAndFailureFallsBack(int tierValue, uint alias)
    {
        var tier = (OwnerConsoleNotificationTier)tierValue;
        var output = new SoundOutput();
        var settings = new OwnerConsoleNotificationSettings
        { CalmSoundFile = "custom.wav", AttentionSoundFile = "custom.wav", AlarmSoundFile = "custom.wav" };
        var checkedPaths = new List<string>();
        var notifier = new SystemOwnerConsoleNotifier(output, settings, path => { checkedPaths.Add(path); return true; });
        notifier.Notify(tier);
        Assert.Equal(["custom.wav"], checkedPaths);
        Assert.Equal(["custom.wav"], output.Files);
        Assert.Empty(output.SystemSounds);
        output.Success = false;
        notifier.Notify(tier);
        Assert.Equal(["custom.wav", "custom.wav"], output.Files);
        Assert.Equal([alias], output.SystemSounds);
    }

    [Fact]
    public void MissingFileUsesSystemSoundWithoutTryingTheFile()
    {
        var output = new SoundOutput();
        var checkedPaths = new List<string>();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { AlarmSoundFile = "missing.wav" },
            path => { checkedPaths.Add(path); return false; });
        notifier.Notify(OwnerConsoleNotificationTier.Alarm);
        Assert.Equal(["missing.wav"], checkedPaths);
        Assert.Empty(output.Files);
        Assert.Equal([0x10u], output.SystemSounds);
    }

    [Fact]
    public void FileCheckFailureFallsBackAndNullNotifierHasNoOutput()
    {
        var output = new SoundOutput();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { CalmSoundFile = "custom.wav" },
            _ => throw new IOException("file unavailable"));
        notifier.Notify(OwnerConsoleNotificationTier.Calm);
        Assert.Equal([0x40u], output.SystemSounds);
        Assert.Empty(output.Files);
        output.SystemSounds.Clear();
        new NullOwnerConsoleNotifier().Notify(OwnerConsoleNotificationTier.Alarm);
        Assert.Empty(output.SystemSounds);
        Assert.Empty(output.Files);
    }

    private sealed class SoundOutput : IOwnerConsoleSoundOutput
    {
        internal readonly List<uint> SystemSounds = [];
        internal readonly List<string> Files = [];
        internal bool Success = true;
        public void PlaySystemSound(uint messageBeepType) => SystemSounds.Add(messageBeepType);
        public bool TryPlayFile(string path) { Files.Add(path); return Success; }
    }
}
