using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class WindowsOwnerConsoleSoundOutput : IOwnerConsoleSoundOutput
{
    public void PlaySystemSound(uint messageBeepType)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { MessageBeep(messageBeepType); }
        catch { }
    }

    public bool TryPlayFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // SND_FILENAME | SND_ASYNC | SND_NODEFAULT: fallback belongs to the notifier.
        try { return PlaySound(path, IntPtr.Zero, 0x00020000u | 0x0001u | 0x0002u); }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
