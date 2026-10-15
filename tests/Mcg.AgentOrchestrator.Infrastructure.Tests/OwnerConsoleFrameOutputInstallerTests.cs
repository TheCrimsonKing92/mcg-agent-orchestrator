using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
        Assert.IsAssignableFrom<AnsiComponentFactory>(factory);
        Assert.Equal(DriverRegistry.Names.ANSI, factory.GetDriverName());
        if (OperatingSystem.IsWindows())
            Assert.Equal(DriverRegistry.GetDefaultDriver().Name, factory.GetDriverName());
        Assert.Equal("", diagnostic.ToString());
        Assert.Null(app.Driver); // Installation does not initialize native input/output.
    }

    [Fact]
    public void InstalledFactoryKeepsAnsiInputAndComposesOutputThroughFrameAdapter()
    {
        using var terminal = new OwnerConsoleFrameReproductionTests.RecordingOutput(20, 4);
        var factory = new OwnerConsoleFrameOutputInstaller.FrameFactory(TextWriter.Null, terminal);
        var builtin = new AnsiComponentFactory();

        Assert.Equal(builtin.CreateInputProcessor(new ConcurrentQueue<char>()).GetType(),
            factory.CreateInputProcessor(new ConcurrentQueue<char>()).GetType());
        var output = Assert.IsType<OwnerConsoleFrameOutput>(factory.CreateOutput());
        Assert.Same(terminal, output.TerminalOutput);
        output.Write(OwnerConsoleFrameCompatibilityTests.Buffer(["whole frame         "]));
        Assert.Single(terminal.Operations);

        factory.AppModel = AppModel.Inline;
        Assert.Same(terminal, factory.CreateOutput());
    }

    [Fact]
    public void WrappedAnsiOutputKeepsBuiltInAnsiSizeMonitor()
    {
        // Bypass AnsiOutput's terminal-acquiring constructor. Size monitoring only needs its
        // size fields; this must not activate an alternate screen or change console modes.
        var terminal = (AnsiOutput)RuntimeHelpers.GetUninitializedObject(typeof(AnsiOutput));
        terminal.SetSize(20, 4);
        var factory = new OwnerConsoleFrameOutputInstaller.FrameFactory(TextWriter.Null, terminal);
        var buffer = new OutputBufferImpl();
        buffer.SetSize(20, 4);
        var builtinMonitor = new AnsiComponentFactory().CreateSizeMonitor(terminal, buffer);

        var monitor = factory.CreateSizeMonitor(factory.CreateOutput(), buffer);

        Assert.Equal("AnsiSizeMonitor", builtinMonitor.GetType().Name);
        Assert.Equal(builtinMonitor.GetType(), monitor.GetType());
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
