namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class SystemOwnerConsoleNotifier(IOwnerConsoleSoundOutput output,
    OwnerConsoleNotificationSettings settings, Func<string, bool> fileExists) : IOwnerConsoleNotifier
{
    public void Notify(OwnerConsoleNotificationTier tier)
    {
        var path = tier switch
        {
            OwnerConsoleNotificationTier.Calm => settings.CalmSoundFile,
            OwnerConsoleNotificationTier.Attention => settings.AttentionSoundFile,
            OwnerConsoleNotificationTier.Alarm => settings.AlarmSoundFile,
            _ => null
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && fileExists(path) && output.TryPlayFile(path)) return;
        }
        catch { /* File failure still falls back to the tier's system sound. */ }
        output.PlaySystemSound(tier switch
        {
            OwnerConsoleNotificationTier.Calm => 0x40u,
            OwnerConsoleNotificationTier.Attention => 0x30u,
            OwnerConsoleNotificationTier.Alarm => 0x10u,
            _ => 0x40u
        });
    }
}
