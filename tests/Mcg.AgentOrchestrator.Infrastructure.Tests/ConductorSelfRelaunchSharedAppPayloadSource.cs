using System.Reflection;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;

// Owns selection of candidate build output; never modifies that output or shared process state.
internal sealed class ConductorSelfRelaunchSharedAppPayloadSource(
    string appOutputDirectory,
    IReadOnlyList<ConductorSelfRelaunchSharedAppPayloadSource.ProjectSource> projects,
    Func<string, TimeSpan, (int ExitCode, string Stdout, string Stderr, bool TimedOut)> buildRunner)
{
    private const string AppName = "Mcg.AgentOrchestrator.App";

    internal sealed record ProjectSource(
        string AssemblyName,
        IReadOnlyList<string> SourceFiles,
        string? DependencyFile);

    internal static ConductorSelfRelaunchSharedAppPayloadSource ForTestAssembly(string repositoryRoot)
    {
        var assembly = typeof(ConductorSelfRelaunchSharedAppPayload).Assembly;
        var configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        if (string.IsNullOrWhiteSpace(configuration))
            throw new SharedAppPayloadApparatusException("test assembly has no build configuration");

        var testDirectory = Path.GetDirectoryName(assembly.Location)!;
        var project = Path.Combine(repositoryRoot, "src", AppName, AppName + ".csproj");
        var dotnet = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet";
        return ForBuildOutput(repositoryRoot, testDirectory, configuration, (destination, timeout) => ConductorSelfRelaunch.RunProcessForTests(
            dotnet,
            ["build", project, "--configuration", configuration, "--nologo", "--output", destination,
                "-v", "quiet", "-clp:ErrorsOnly"],
            repositoryRoot,
            timeout));
    }

    internal static ConductorSelfRelaunchSharedAppPayloadSource ForBuildOutput(
        string repositoryRoot, string testDirectory, string configuration,
        Func<string, TimeSpan, (int ExitCode, string Stdout, string Stderr, bool TimedOut)> buildRunner)
        => new(ResolveAppOutputDirectory(repositoryRoot, testDirectory, configuration),
            ReadProjectClosure(Path.Combine(repositoryRoot, "src", AppName, AppName + ".csproj"),
                repositoryRoot, testDirectory, configuration), buildRunner);

    internal static string ResolveAppOutputDirectory(
        string repositoryRoot, string testAssemblyDirectory, string configuration)
        => ResolveProjectOutputDirectory(repositoryRoot, testAssemblyDirectory, configuration, AppName);

    private static string ResolveProjectOutputDirectory(
        string repositoryRoot, string testAssemblyDirectory, string configuration, string projectName)
    {
        var directory = new DirectoryInfo(testAssemblyDirectory);
        // Directory.Build.props: <artifacts>/bin/<Project>/<Configuration>, without a TFM segment.
        if (directory.Name.Equals(configuration, StringComparison.OrdinalIgnoreCase)
            && directory.Parent?.Name == "Mcg.AgentOrchestrator.Infrastructure.Tests"
            && directory.Parent.Parent is { } binDirectory)
            return Path.Combine(binDirectory.FullName, projectName, configuration);

        // SDK default: <repo>/tests/<Project>/bin/<Configuration>/<TFM>.
        if (directory.Name.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && directory.Parent?.Name.Equals(configuration, StringComparison.OrdinalIgnoreCase) == true
            && directory.Parent.Parent?.Name == "bin")
            return Path.Combine(repositoryRoot, "src", projectName, "bin", configuration, directory.Name);

        var location = TestBuildOutputLocator.Locate(projectName, configuration, testAssemblyDirectory);
        if (location.Directory is { } outputDirectory) return outputDirectory;
        throw new SharedAppPayloadApparatusException(
            $"unrecognized test assembly output layout directory={testAssemblyDirectory} configuration={configuration}"
            + $" tried={string.Join("; ", location.TriedPaths)}");
    }

    private static IReadOnlyList<ProjectSource> ReadProjectClosure(
        string appProject, string repositoryRoot, string testDirectory, string configuration)
    {
        var projects = new List<ProjectSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(appProject);
        return projects;

        void Visit(string project)
        {
            project = Path.GetFullPath(project);
            if (!seen.Add(project)) return;
            var document = XDocument.Load(project);
            var projectDirectory = Path.GetDirectoryName(project)!;
            var projectName = Path.GetFileNameWithoutExtension(project);
            var assemblyName = document.Descendants("AssemblyName").LastOrDefault()?.Value ?? projectName;
            var sources = EnumerateSourceFiles(projectDirectory).ToList();
            // Shared build inputs and linked content are outside the project directories.
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "global.json" })
            {
                var path = Path.Combine(repositoryRoot, name);
                if (File.Exists(path)) sources.Add(path);
            }
            foreach (var content in document.Descendants().Where(element =>
                element.Name.LocalName is "Content" or "None"
                && element.Element("CopyToOutputDirectory")?.Value is "Always" or "PreserveNewest"))
            {
                var include = content.Attribute("Include")?.Value ?? content.Attribute("Update")?.Value;
                if (include is not null)
                    sources.Add(Path.GetFullPath(Path.Combine(projectDirectory, include)));
            }

            // A shared --output build also emits library deps.json files that App's own bin omits.
            // OperatorComms opts out with GenerateDependencyFile=false; don't invent that artifact.
            var generatesDependencies = !string.Equals(
                document.Descendants("GenerateDependencyFile").LastOrDefault()?.Value,
                "false", StringComparison.OrdinalIgnoreCase);
            var dependencyFile = assemblyName != AppName && generatesDependencies
                ? Path.Combine(ResolveProjectOutputDirectory(repositoryRoot, testDirectory, configuration, projectName),
                    assemblyName + ".deps.json")
                : null;
            projects.Add(new(assemblyName, sources, dependencyFile));
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (include is not null) Visit(Path.Combine(projectDirectory, include));
            }
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory)) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj" or "artifacts" or ".scratch") continue;
            foreach (var file in EnumerateSourceFiles(child)) yield return file;
        }
    }

    internal void AssembleInto(string destination)
    {
        if (IsCurrent())
        {
            foreach (var file in Directory.EnumerateFiles(appOutputDirectory, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) is LandingAppBuildStore.HeadMarkerName or LandingAppBuildStore.CompleteMarkerName)
                    continue;
                Copy(file, Path.GetRelativePath(appOutputDirectory, file));
            }
            foreach (var project in projects)
                if (project.DependencyFile is { } dependencyFile) Copy(dependencyFile, Path.GetFileName(dependencyFile));
            return;
        }

        (int ExitCode, string Stdout, string Stderr, bool TimedOut) build;
        try
        {
            // The host/gate owns hang detection. Host load must not turn an App build into a failed fact.
            build = buildRunner(destination, Timeout.InfiniteTimeSpan);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SharedAppPayloadApparatusException(
                $"build could not run appOutput={appOutputDirectory}: {exception.Message}", exception);
        }
        if (build.ExitCode != 0 || build.TimedOut)
            throw new SharedAppPayloadApparatusException(
                $"build failed appOutput={appOutputDirectory} exit={build.ExitCode} timedOut={build.TimedOut}: "
                + build.Stderr[^Math.Min(build.Stderr.Length, 1000)..]);
        if (!File.Exists(Path.Combine(destination, AppName + ".dll")))
            throw new SharedAppPayloadApparatusException(
                $"build produced no {AppName}.dll appOutput={appOutputDirectory} exit={build.ExitCode} timedOut={build.TimedOut}");

        void Copy(string source, string relativePath)
        {
            var target = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }

    private bool IsCurrent()
    {
        if (projects.Count == 0 || !projects.Any(project => project.AssemblyName == AppName)) return false;
        foreach (var name in new[] { AppName + ".dll", AppName + ".deps.json", AppName + ".runtimeconfig.json",
            AppName + (OperatingSystem.IsWindows() ? ".exe" : "") })
            if (!File.Exists(Path.Combine(appOutputDirectory, name))) return false;

        foreach (var project in projects)
        {
            var assembly = Path.Combine(appOutputDirectory, project.AssemblyName + ".dll");
            if (!File.Exists(assembly) || project.DependencyFile is { } dependencies && !File.Exists(dependencies))
                return false;
            var builtAt = File.GetLastWriteTimeUtc(assembly);
            if (project.SourceFiles.Any(source => !File.Exists(source) || File.GetLastWriteTimeUtc(source) > builtAt))
                return false;
        }
        return true;
    }
}

internal sealed class SharedAppPayloadApparatusException(string reason, Exception? innerException = null)
    : InvalidOperationException("Shared App payload apparatus failure: " + reason, innerException);
