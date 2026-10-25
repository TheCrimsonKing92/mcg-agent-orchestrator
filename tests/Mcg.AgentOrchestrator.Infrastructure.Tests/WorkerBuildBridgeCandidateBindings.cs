using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

// This is a fixture binding, not a general MSBuild evaluator. Unsupported metadata fails closed.
internal sealed class WorkerBuildBridgeCandidateBindings
{
    internal sealed record Identity(string Name, string FullName, Guid Mvid, string Sha256);
    internal sealed record Binding(string Project, string ProjectSha256, string HostPath, Identity Assembly);
    internal sealed record Graph(string Root, string ToolProject, IReadOnlyList<string> Projects,
        IReadOnlyList<XElement> Packages, IReadOnlyDictionary<string, string> Inputs);
    internal sealed record FileState(string Sha256, long Length, DateTime LastWriteTimeUtc);

    internal Graph SourceGraph { get; }
    internal string Generation { get; }
    internal IReadOnlyList<Binding> Bindings { get; }
    internal string HostSnapshotSha256 { get; }
    internal string? RecheckedHostSnapshotSha256 { get; private set; }
    private readonly Dictionary<string, FileState> _hostFiles;

    internal WorkerBuildBridgeCandidateBindings(string candidate)
    {
        SourceGraph = ReadGraph(candidate);
        var execution = typeof(WorkerBuildProjectScope).Assembly;
        var host = typeof(MtpTestRunnerScriptTestsWorkerBuildNoBuildBridge).Assembly;
        Generation = Path.GetDirectoryName(Canonical(execution.Location))!;
        Require(Path.GetDirectoryName(Canonical(host.Location)) == Generation, "Host and Execution generations differ.");
        var receiptPath = Path.Combine(Generation, ".mcg-build-receipt.txt");
        var receipt = File.ReadAllLines(receiptPath).Select(line => line.TrimEnd('\r').Split('\t')).ToArray();
        string Header(string key) => receipt.Single(row => row.Length >= 2 && row[0] == "K" && row[1] == key).ElementAtOrDefault(2) ?? "";
        Require(Header("schemaVersion") == "2", "Unsupported host receipt.");
        Require(Path.GetFullPath(Header("repositoryRoot")).TrimEnd(Path.DirectorySeparatorChar).Equals(SourceGraph.Root, StringComparison.OrdinalIgnoreCase), "Host receipt belongs to another candidate.");
        Require(Header("assemblyLeaf") == Path.GetFileName(host.Location) &&
            Header("assemblySha256").Equals(Hash(host.Location), StringComparison.OrdinalIgnoreCase), "Host receipt assembly mismatch.");
        Require(ReadIdentity(host.Location).Mvid == host.ManifestModule.ModuleVersionId, "Loaded host MVID mismatch.");
        // Native no-build publication may relocate the whole verified closure. Rebase only relative
        // receipt closure paths; never open the original build directory or search for an assembly.
        var recordedOutput = Path.GetFullPath(Header("outputDirectory"));
        var closure = receipt.Where(row => row.Length == 4 && row[0] == "F" && row[1] == "closure")
            .ToDictionary(row => SafeRelative(recordedOutput, row[2]), row => row[3], StringComparer.OrdinalIgnoreCase);
        Require(closure.Count > 0, "Host receipt closure is empty.");
        foreach (var pair in closure)
            Require(Hash(Canonical(Path.Combine(Generation, pair.Key))).Equals(pair.Value, StringComparison.OrdinalIgnoreCase), "Host closure hash mismatch: " + pair.Key);
        var sealPath = Path.Combine(Generation, ".mcg-mtp-closure.json");
        if (File.Exists(sealPath))
        {
            using var seal = JsonDocument.Parse(File.ReadAllText(sealPath));
            Require(seal.RootElement.GetProperty("schemaVersion").GetInt32() == 1 && seal.RootElement.GetProperty("project").GetString() == host.GetName().Name, "Invalid native host closure seal.");
        }
        HostReceiptClosureFileSet.RequireEqual(Generation, sealPath, closure.Keys);
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(host.Location, ".deps.json")));
        var libraries = deps.RootElement.GetProperty("libraries");
        foreach (var package in SourceGraph.Packages)
            Require(libraries.TryGetProperty(package.Attribute("Include")!.Value + "/" + package.Attribute("Version")!.Value, out var library) && library.GetProperty("type").GetString() == "package", "Candidate package does not match the host generation.");
        Bindings = SourceGraph.Projects.Select(project =>
        {
            var name = AssemblyNameFor(project);
            var path = Canonical(Path.Combine(Generation, name + ".dll"));
            Require(Path.GetDirectoryName(path) == Generation && closure.ContainsKey(name + ".dll"), "Dependency escapes verified host closure: " + name);
            var identity = ReadIdentity(path);
            Require(identity.Name == name, "Conflicting assembly identity: " + name);
            Require(libraries.EnumerateObject().Count(item => item.Name.StartsWith(name + "/", StringComparison.Ordinal) && item.Value.GetProperty("type").GetString() == "project") == 1,
                "Dependency is not a unique host project: " + name);
            var loaded = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(identity.FullName));
            Require(Canonical(loaded.Location) == path && loaded.ManifestModule.ModuleVersionId == identity.Mvid, "Loaded dependency generation mismatch: " + name);
            return new Binding(project, Hash(project), path, identity);
        }).ToArray();
        Require(Bindings.Select(binding => binding.Assembly.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Bindings.Count, "Conflicting project assembly names.");
        Require(Bindings.Any(binding => binding.HostPath == Canonical(execution.Location)), "Tool graph is not anchored to Execution.");
        _hostFiles = Snapshot(Generation);
        HostSnapshotSha256 = SnapshotHash(_hostFiles);
    }

    internal static Graph ReadGraph(string candidate)
    {
        candidate = Canonical(candidate);
        var tool = Path.Combine(candidate, "scripts", "WorkerBuildScopeTool", "WorkerBuildScopeTool.csproj");
        var projects = new List<string>();
        var packages = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void InspectAncestors(string project)
        {
            for (var directory = Path.GetDirectoryName(project); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                foreach (var leaf in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                {
                    var path = Path.Combine(directory, leaf);
                    if (!File.Exists(path)) continue;
                    Require(leaf != "Directory.Packages.props", "Central package metadata is unsupported.");
                    var xml = XDocument.Load(path);
                    Require(!xml.Descendants().Any(element => element.Name.LocalName is "Import" or "ProjectReference" or "PackageReference" or "Reference" or "AssemblyName" or "ManagePackageVersionsCentrally"),
                        "Imported reference metadata is unsupported: " + path);
                    inputs[path] = Hash(path);
                }
                // The candidate is the explicit evaluation boundary, not an ambient checkout.
                if (string.Equals(directory, candidate, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        void Visit(string project, bool isTool)
        {
            project = Path.GetFullPath(project);
            _ = SafeRelative(candidate, project);
            project = Canonical(project);
            Require(!visiting.Contains(project), "Project reference cycle.");
            if (!visited.Add(project)) return;
            visiting.Add(project);
            InspectAncestors(project);
            inputs[project] = Hash(project);
            var xml = XDocument.Load(project);
            Require(xml.Root?.Attribute("Sdk")?.Value == "Microsoft.NET.Sdk" &&
                !xml.Descendants().Any(element => element.Name.LocalName is "Import" or "Target" or "Choose" or "Reference" or "ManagePackageVersionsCentrally"), "Unsupported project metadata: " + project);
            Require(xml.Descendants("PropertyGroup").Elements().All(element =>
                (element.Name.LocalName is "TargetFramework" or "ImplicitUsings" or "Nullable" or "OutputType" or "AssemblyName") &&
                !element.AncestorsAndSelf().Attributes("Condition").Any() && IsLiteral(element.Value)), "Unsupported project properties: " + project);
            _ = AssemblyNameFor(project);
            foreach (var reference in xml.Descendants().Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference"))
            {
                Require(reference.Name.Namespace == XNamespace.None && reference.Parent?.Name == "ItemGroup" &&
                    !reference.AncestorsAndSelf().Attributes("Condition").Any(), "Conditional reference metadata is unsupported.");
                var include = reference.Attribute("Include")?.Value;
                Require(IsLiteral(include), "Nonliteral reference metadata is unsupported.");
                if (reference.Name == "ProjectReference")
                {
                    Require(reference.Attributes().Count() == 1 && !reference.HasElements, "Unsupported ProjectReference metadata.");
                    Visit(Path.Combine(Path.GetDirectoryName(project)!, include!.Replace('\\', Path.DirectorySeparatorChar)), false);
                }
                else
                {
                    Require(reference.Attributes().All(attribute => attribute.Name == "Include" || attribute.Name == "Version") &&
                        reference.Attributes().Count() == 2 && !reference.HasElements && IsLiteral(reference.Attribute("Version")?.Value), "Unsupported PackageReference metadata.");
                    if (packages.TryGetValue(include!, out var previous))
                        Require(XNode.DeepEquals(previous, reference), "Conflicting package declarations: " + include);
                    else packages.Add(include!, new XElement(reference));
                }
            }
            visiting.Remove(project);
            if (!isTool) projects.Add(project);
        }
        Visit(tool, true);
        inputs[Path.Combine(candidate, "scripts", "WorkerBuildScopeTool", "Program.cs")] = Hash(Path.Combine(candidate, "scripts", "WorkerBuildScopeTool", "Program.cs"));
        var global = Path.Combine(candidate, "global.json");
        if (File.Exists(global)) inputs[global] = Hash(global);
        return new Graph(candidate, tool, projects, packages.Values.ToArray(), inputs);
    }

    internal XDocument Transform(string? omittedAssembly = null)
    {
        var project = XDocument.Load(SourceGraph.ToolProject);
        project.Descendants("ProjectReference").Remove();
        project.Descendants("PackageReference").Remove();
        project.Root!.Add(new XElement("ItemGroup", Bindings.Where(binding => binding.Assembly.Name != omittedAssembly).Select(binding =>
            new XElement("Reference", new XAttribute("Include", binding.Assembly.FullName), new XElement("HintPath", binding.HostPath), new XElement("Private", "true"))),
            SourceGraph.Packages.Select(package => new XElement(package))));
        return project;
    }

    internal void WriteManifest(string path, string? omittedAssembly) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        Generation, SourceGraph.Inputs, bindings = Bindings.Where(binding => binding.Assembly.Name != omittedAssembly), omittedAssembly
    }));

    internal void WriteFixture(string root, string? omittedAssembly = null, bool invalidSource = false)
    {
        var directory = Path.Combine(root, "scripts", "WorkerBuildScopeTool");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(SourceGraph.Root, "scripts", "WorkerBuildScopeTool", "Program.cs"), Path.Combine(directory, "Program.cs"));
        Transform(omittedAssembly).Save(Path.Combine(directory, "WorkerBuildScopeTool.csproj"));
        WriteManifest(Path.Combine(directory, "candidate-bindings.json"), omittedAssembly);
        if (invalidSource) File.AppendAllText(Path.Combine(directory, "Program.cs"), "\n#error BRIDGE_INVALID_SCOPE_SOURCE\n");
        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "global.json" })
            File.Copy(Path.Combine(SourceGraph.Root, file), Path.Combine(root, file));
    }

    internal void VerifyHostUnchanged()
    {
        var current = Snapshot(Generation);
        Require(_hostFiles.Count == current.Count && _hostFiles.All(pair => current.TryGetValue(pair.Key, out var state) && state == pair.Value), "Host output changed during bridge execution. " + HostReceiptClosureFileSet.DescribeChanges(_hostFiles, current));
        foreach (var binding in Bindings) Require(ReadIdentity(binding.HostPath) == binding.Assembly, "Source host identity changed.");
        foreach (var input in SourceGraph.Inputs) Require(Hash(input.Key) == input.Value, "Candidate graph changed during bridge execution.");
        RecheckedHostSnapshotSha256 = SnapshotHash(current);
    }

    internal static Identity ReadIdentity(string path)
    {
        path = Canonical(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var name = AssemblyName.GetAssemblyName(path);
        return new Identity(name.Name!, name.FullName, metadata.GetGuid(metadata.GetModuleDefinition().Mvid), Hash(path));
    }

    private static Dictionary<string, FileState> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => SafeRelative(directory, path), path => new FileState(Hash(path), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)), StringComparer.OrdinalIgnoreCase);

    private static string SnapshotHash(Dictionary<string, FileState> snapshot) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal))));

    private static string AssemblyNameFor(string project)
    {
        var declarations = XDocument.Load(project).Descendants("AssemblyName").ToArray();
        Require(declarations.Length <= 1 && declarations.All(element => !element.AncestorsAndSelf().Attributes("Condition").Any() && IsLiteral(element.Value)), "Unsupported assembly name metadata.");
        return declarations.SingleOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(project);
    }

    private static bool IsLiteral(string? value) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['$', '@', '%', '*', '?', ';']) < 0;
    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static string Canonical(string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Redirected binding path: " + current);
        return path;
    }
    internal static string SafeRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        Require(!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Path escapes candidate generation.");
        return relative;
    }
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
