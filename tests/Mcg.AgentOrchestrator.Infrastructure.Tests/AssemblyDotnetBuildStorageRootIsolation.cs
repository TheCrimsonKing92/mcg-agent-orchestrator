using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class AssemblyDotnetBuildStorageRootIsolation
{
    // Keep the claim and its owner lock alive until process exit.
    private static IsolatedDotnetRootFixture? _baselineFixture;

    internal static string? BaselineRoot { get; private set; }

    internal static string DefaultRoot() => Path.GetFullPath(DotnetBuildStorageLayout.ResolveIsolatedRootBase(
        null,
        Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetTempPath(),
        OperatingSystem.IsWindows()));

    internal static bool ShouldReplace(string? inherited, string defaultRoot) =>
        string.IsNullOrWhiteSpace(inherited) || string.Equals(
            Path.GetFullPath(inherited).TrimEnd('\\', '/'),
            Path.GetFullPath(defaultRoot).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [ModuleInitializer]
    internal static void Install()
    {
        try
        {
            var inherited = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
            if (ShouldReplace(inherited, DefaultRoot()))
            {
                // Reclaim diagnostics must not turn a successful fallback into a startup failure.
                Action<string> diagnostic = _ => { };
                try
                {
                    _baselineFixture = new IsolatedDotnetRootFixture(IsolatedDotnetRootFixture.GetWindowsBasePath(), diagnostic);
                }
                catch
                {
                    _baselineFixture = new IsolatedDotnetRootFixture(null, diagnostic);
                }
            }

            BaselineRoot = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        }
        catch
        {
            BaselineRoot = null;
            try { Console.Error.WriteLine("test-build-storage-root baseline-failed"); }
            catch { }
        }
    }
}
