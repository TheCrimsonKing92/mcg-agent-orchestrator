using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// The single approved private-reflection seam, pinned to Terminal.Gui 2.5.0.
internal static class OwnerConsoleFrameOutputInstaller
{
    private const string FactoryMember = "Terminal.Gui.App.ApplicationImpl._componentFactory";

    internal static IApplication CreateApplication()
    {
        var app = Terminal.Gui.App.Application.Create();
        if (OperatingSystem.IsWindows() &&
            (string.IsNullOrEmpty(app.ForceDriver) || app.ForceDriver == DriverRegistry.Names.WINDOWS))
            TryInstall(app, System.Console.Error);
        return app;
    }

    // Shared with the upgrade guard so every reflected signature is checked by name.
    internal static FieldInfo RequireFactoryMember(Type implementation)
    {
        if (implementation.FullName != "Terminal.Gui.App.ApplicationImpl" ||
            !typeof(IApplication).IsAssignableFrom(implementation))
            throw new MissingMemberException(FactoryMember);
        var field = implementation.GetField("_componentFactory", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null || field.FieldType != typeof(IComponentFactory) || !field.IsPrivate || !field.IsInitOnly)
            throw new MissingMemberException(FactoryMember);
        return field;
    }

    internal static bool TryInstall(IApplication app, TextWriter diagnostic, Type? implementation = null)
    {
        try
        {
            var field = RequireFactoryMember(implementation ?? app.GetType());
            if (field.GetValue(app) is not null)
                throw new InvalidOperationException($"{FactoryMember} is already installed.");
            field.SetValue(app, new FrameFactory(diagnostic));
            return true;
        }
        catch (Exception ex) when (ex is MemberAccessException or ArgumentException or NotSupportedException or TargetException)
        {
            diagnostic.WriteLine($"Console frame buffering unavailable: {FactoryMember}: {ex.Message}");
            return false;
        }
    }

    internal sealed class FrameFactory(TextWriter diagnostic) : WindowsComponentFactory
    {
        public override IOutput CreateOutput() => BufferOutput(base.CreateOutput(), diagnostic);
    }

    internal static IOutput BufferOutput(IOutput output, TextWriter diagnostic)
    {
        if (output.IsLegacyConsole)
        {
            diagnostic.WriteLine("Console frame buffering unavailable: WindowsOutput.IsLegacyConsole requires native cursor and attribute writes.");
            return output;
        }
        return new OwnerConsoleFrameOutput(output) { Force16Colors = output.Force16Colors };
    }
}
