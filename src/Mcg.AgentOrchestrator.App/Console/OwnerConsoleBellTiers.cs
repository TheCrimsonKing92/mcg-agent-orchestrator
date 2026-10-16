namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal readonly record struct OwnerConsoleBellTiers(bool Calm, bool Attention, bool Alarm)
{
    internal static OwnerConsoleBellTiers All => new(true, true, true);
    internal static OwnerConsoleBellTiers None => new(false, false, false);
    internal bool IsOn(OwnerConsoleNotificationTier tier) => tier switch
    {
        OwnerConsoleNotificationTier.Calm => Calm,
        OwnerConsoleNotificationTier.Attention => Attention,
        OwnerConsoleNotificationTier.Alarm => Alarm,
        _ => false
    };
    internal OwnerConsoleBellTiers With(OwnerConsoleNotificationTier tier, bool on) => tier switch
    {
        OwnerConsoleNotificationTier.Calm => this with { Calm = on },
        OwnerConsoleNotificationTier.Attention => this with { Attention = on },
        OwnerConsoleNotificationTier.Alarm => this with { Alarm = on },
        _ => this
    };
    internal string StatusText => this == All ? "on" : this == None ? "off" :
        $"calm {(Calm ? "on" : "off")}, attention {(Attention ? "on" : "off")}, alarm {(Alarm ? "on" : "off")}";
}
