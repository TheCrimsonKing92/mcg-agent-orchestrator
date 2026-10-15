using System.Reflection;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

// Parallel-safe: instance applications are never initialized; no native terminal operations.
public sealed class OwnerConsoleFrameOutputInstallerTests
{
    [Fact]
    public void TerminalGui250ApplicationImplComponentFactorySignatureUpgradeGuard()
    {
        using var app = Terminal.Gui.App.Application.Create();
        var field = OwnerConsoleFrameOutputInstaller.RequireFactoryMember(app.GetType());
        Assert.Equal("Terminal.Gui.App.ApplicationImpl", field.DeclaringType!.FullName);
        Assert.Equal("_componentFactory", field.Name);
        Assert.Equal(typeof(IComponentFactory), field.FieldType);
        Assert.True(field.IsPrivate);
        Assert.True(field.IsInitOnly);
        Assert.False(field.IsStatic);
        Assert.Null(field.GetValue(app));
        using var diagnostic = new StringWriter();

        Assert.True(OwnerConsoleFrameOutputInstaller.TryInstall(app, diagnostic));

        var factory = Assert.IsType<OwnerConsoleFrameOutputInstaller.FrameFactory>(field.GetValue(app));
        Assert.Equal(DriverRegistry.Names.WINDOWS, factory.GetDriverName());
        Assert.Equal("", diagnostic.ToString());
        Assert.Null(app.Driver); // Installation does not initialize native input/output.
    }

    [Fact]
    public void MissingInternalMemberLeavesBuiltInApplicationUntouchedAndNamesMemberOnce()
    {
        using var app = Terminal.Gui.App.Application.Create();
        using var diagnostic = new StringWriter();

        Assert.False(OwnerConsoleFrameOutputInstaller.TryInstall(app, diagnostic, typeof(object)));

        Assert.Null(OwnerConsoleFrameOutputInstaller.RequireFactoryMember(app.GetType()).GetValue(app));
        var line = Assert.Single(diagnostic.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Terminal.Gui.App.ApplicationImpl._componentFactory", line);
        Assert.Null(app.Driver);
    }

    [Fact]
    public void SignatureMismatchFailsClosedByMemberName()
    {
        var error = Assert.Throws<MissingMemberException>(() =>
            OwnerConsoleFrameOutputInstaller.RequireFactoryMember(typeof(OwnerConsoleFrameOutputInstallerTests)));
        Assert.Contains("Terminal.Gui.App.ApplicationImpl._componentFactory", error.Message);
    }

    [Fact]
    public void LegacyConsoleKeepsUnmodifiedNativeOutputAndReportsCapabilityOnce()
    {
        using var native = new OwnerConsoleFrameReproductionTests.RecordingOutput(20, 4) { IsLegacyConsole = true };
        using var diagnostic = new StringWriter();

        Assert.Same(native, OwnerConsoleFrameOutputInstaller.BufferOutput(native, diagnostic));

        Assert.Contains("WindowsOutput.IsLegacyConsole", Assert.Single(
            diagnostic.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)));
    }
}
