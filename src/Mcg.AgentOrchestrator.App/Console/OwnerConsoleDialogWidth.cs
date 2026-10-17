namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleDialogWidth
{
    public const int Percent = 85;

    // Terminal.Gui 2.5.0: Layout/DimPercent truncates size * (percent / 100f).
    // DialogTResult uses a one-cell border on each side and no horizontal padding.
    // Adornment/MarginView reserves one right column for the default shadow; retain
    // that reserve even in shadow-free themes. View.ScrollBars creates bars lazily;
    // ListView and OwnerConsoleTextDialog never request one, so the body reserves 0.
    // Text columns = floor(screen * 85%) - 1 shadow - 2 borders - 0 scrollbar:
    // 80 => 65, 120 => 99, 200 => 167.
    internal static int? TextColumns(int screenWidth) => screenWidth <= 0 ? null :
        (int)(screenWidth * (Percent / 100f)) - 3;

    internal static int ForDiagram(int? available) => available is { } width ?
        Math.Max(width, OwnerFleetDiagram.MinimumWidth) : OwnerFleetDiagram.DefaultWidth;
}
