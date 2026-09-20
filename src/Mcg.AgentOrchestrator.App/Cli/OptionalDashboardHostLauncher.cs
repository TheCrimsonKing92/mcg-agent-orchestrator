using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class OptionalDashboardHostLauncher
{
    internal const int MissingComponentExitCode = 78;
    private const string ComponentName = "Mcg.AgentOrchestrator.Dashboard";

    public static int RunStatic(
        IReadOnlyList<string> args,
        CliExecutionContext context,
        string? baseDirectory = null,
        TextWriter? error = null)
    {
        error ??= Console.Error;
        var assembly = LoadComponentAssembly(baseDirectory, error);
        if (assembly is null)
            return MissingComponentExitCode;

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "dashboard":
                    RenderDashboard(assembly, args, context);
                    return 0;
                case "transcript":
                    RenderTranscript(assembly, args, context);
                    return 0;
                default:
                    throw new ArgumentException($"Unsupported static dashboard command '{args[0]}'.");
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    public static int Run(IReadOnlyList<string> args, string? baseDirectory = null, TextWriter? error = null)
    {
        error ??= Console.Error;
        var directory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var executable = Path.Combine(directory, $"{ComponentName}.exe");
        var assembly = Path.Combine(directory, $"{ComponentName}.dll");
        ProcessStartInfo startInfo;
        string resolvedPath;

        if (File.Exists(executable))
        {
            resolvedPath = executable;
            startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        }
        else if (File.Exists(assembly))
        {
            resolvedPath = assembly;
            startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            startInfo.ArgumentList.Add(assembly);
        }
        else
        {
            error.WriteLine(
                $"Dashboard component is not installed beside the headless runtime. Publish it with scripts/publish-dashboard.ps1 (looked in '{directory}').");
            return MissingComponentExitCode;
        }

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var child = WorkerProcessJobs.StartRegisteredOrThrow(startInfo, "optional-dashboard-host");
            ConsoleCancelEventHandler? handler = null;
            handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The owned-process wrapper may already have observed exit.
                }
            };
            Console.CancelKeyPress += handler;
            try
            {
                child.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult();
                return child.ExitCode;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }
        catch (Exception ex)
        {
            error.WriteLine($"Dashboard component '{resolvedPath}' could not be started: {ex.Message}");
            return MissingComponentExitCode;
        }
    }

    private static Assembly? LoadComponentAssembly(string? baseDirectory, TextWriter error)
    {
        var directory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var assemblyPath = Path.Combine(directory, $"{ComponentName}.dll");
        if (!File.Exists(assemblyPath))
        {
            error.WriteLine(
                $"Dashboard component is not installed beside the headless runtime. Publish it with scripts/publish-dashboard.ps1 (looked in '{directory}').");
            return null;
        }

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, ComponentName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Location, assemblyPath, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
            return loaded;

        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException or BadImageFormatException)
        {
            error.WriteLine($"Dashboard component '{assemblyPath}' could not be loaded: {ex.Message}");
            return null;
        }
    }

    private static void RenderDashboard(
        Assembly assembly,
        IReadOnlyList<string> args,
        CliExecutionContext context)
    {
        var hostType = RequireType(assembly, "Mcg.AgentOrchestrator.App.Dashboard.Hosting.DashboardHost");
        var parseMethod = RequireMethod(hostType, "ParseDashboardArgs", parameterCount: 1);
        var commandArgs = parseMethod.Invoke(null, [args])
            ?? throw new InvalidOperationException("Dashboard argument parsing returned no result.");
        var dashboardPath = RequireProperty(commandArgs, "Path") as string
            ?? throw new InvalidOperationException("Dashboard argument parsing returned no output path.");
        var options = RequireProperty(commandArgs, "Options");
        SetProperty(options, "AgentDefinitions", context.Agents);
        SetProperty(options, "WorkerProfiles", context.WorkerProfiles);

        var rendererType = RequireType(assembly, "Mcg.AgentOrchestrator.App.Dashboard.Rendering.DashboardRenderer");
        var renderMethod = RequireMethod(rendererType, "Render", parameterCount: 3);
        var html = renderMethod.Invoke(null, [context.Kernel, options, null]) as string
            ?? throw new InvalidOperationException("Dashboard rendering returned no content.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dashboardPath))!);
        File.WriteAllText(dashboardPath, html);
        Console.WriteLine($"Dashboard: {Path.GetFullPath(dashboardPath)}");
        if (ReadProperty(options, "AutoRefreshSeconds") is int refreshSeconds && refreshSeconds > 0)
            Console.WriteLine($"Auto-refresh: {refreshSeconds}s");
    }

    private static void RenderTranscript(
        Assembly assembly,
        IReadOnlyList<string> args,
        CliExecutionContext context)
    {
        var goal = context.CurrentGoal
            ?? throw new InvalidOperationException("A current goal is required to render a transcript.");
        var transcriptPath = args.Count > 1
            ? args[1]
            : context.Workspace.TranscriptPath;
        var rendererType = RequireType(assembly, "Mcg.AgentOrchestrator.App.Dashboard.Rendering.GoalTranscriptRenderer");
        var renderMethod = RequireMethod(rendererType, "Render", parameterCount: 5);
        var transcript = renderMethod.Invoke(
            null,
            [context.Kernel, goal, context.WorkerProfiles, context.Agents, context.Workspace.ExecutionDirectory]) as string
            ?? throw new InvalidOperationException("Goal transcript rendering returned no content.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transcriptPath))!);
        File.WriteAllText(transcriptPath, transcript);
        Console.WriteLine($"Transcript: {Path.GetFullPath(transcriptPath)}");
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: false)
        ?? throw new InvalidOperationException($"Dashboard component does not provide required type '{name}'.");

    private static MethodInfo RequireMethod(Type type, string name, int parameterCount) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount)
        ?? throw new InvalidOperationException(
            $"Dashboard component does not provide required method '{type.FullName}.{name}'.");

    private static object RequireProperty(object instance, string name) =>
        ReadProperty(instance, name)
        ?? throw new InvalidOperationException(
            $"Dashboard component did not return required property '{instance.GetType().FullName}.{name}'.");

    private static object? ReadProperty(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name)
            ?? throw new InvalidOperationException(
                $"Dashboard component did not return required property '{instance.GetType().FullName}.{name}'.");
        return property.GetValue(instance);
    }

    private static void SetProperty(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name)
            ?? throw new InvalidOperationException(
                $"Dashboard component did not return required property '{instance.GetType().FullName}.{name}'.");
        property.SetValue(instance, value);
    }
}
