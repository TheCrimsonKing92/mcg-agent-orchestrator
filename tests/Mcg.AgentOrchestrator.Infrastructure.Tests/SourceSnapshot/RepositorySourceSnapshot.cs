using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// A frozen file inventory with one lazily materialized text, line list and tree per file.
internal sealed class RepositorySourceSnapshot
{
    private readonly IReadOnlyDictionary<string, SourceFile> filesByPath;
    internal ISourceSnapshotObserver Observer { get; }
    public string Root { get; }
    public IReadOnlyList<string> Files { get; }

    internal RepositorySourceSnapshot(string root, ISourceSnapshotObserver observer)
    {
        Root = root;
        Observer = observer;
        var paths = new[] { "tests", "src" }
            .Select(area => Path.Combine(root, area))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        Files = Array.AsReadOnly(paths);
        filesByPath = paths.ToDictionary(path => path, path => new SourceFile(root, path, observer),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        observer.OnEnumerated(root);
    }

    public IEnumerable<string> EnumerateFiles(string directory)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(Root, directory));
        var relative = Path.GetRelativePath(Root, full).Replace('\\', '/').TrimEnd('/');
        if (relative != "tests" && relative != "src" &&
            !relative.StartsWith("tests/", StringComparison.Ordinal) && !relative.StartsWith("src/", StringComparison.Ordinal))
            throw new ArgumentException("The directory must be inside the repository tests or src tree.", nameof(directory));
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Source directory does not exist: {full}");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Files.Where(path => path.StartsWith(relative + "/", comparison)).Select(FullPath);
    }

    public string GetText(string path) => FindFile(path).Text.Value;
    public IReadOnlyList<string> GetLines(string path) => FindFile(path).Lines.Value;
    public SyntaxTree GetSyntaxTree(string path) => FindFile(path).Tree.Value;

    private string FullPath(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    private SourceFile FindFile(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : FullPath(path));
        var relative = Path.GetRelativePath(Root, full).Replace('\\', '/');
        if (!filesByPath.TryGetValue(relative, out var file))
            throw new FileNotFoundException("The file is not in the repository source snapshot.", full);
        return file;
    }

    private sealed class SourceFile
    {
        public readonly Lazy<string> Text;
        public readonly Lazy<IReadOnlyList<string>> Lines;
        public readonly Lazy<SyntaxTree> Tree;

        public SourceFile(string root, string relativePath, ISourceSnapshotObserver observer)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Text = new Lazy<string>(() =>
            {
                var text = File.ReadAllText(fullPath);
                observer.OnTextRead(root, relativePath);
                return text;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            Lines = new Lazy<IReadOnlyList<string>>(() =>
            {
                using var reader = new StringReader(Text.Value);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line) lines.Add(line);
                return lines.AsReadOnly();
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            Tree = new Lazy<SyntaxTree>(() =>
            {
                var tree = CSharpSyntaxTree.ParseText(Text.Value, path: fullPath);
                observer.OnParsed(root, relativePath);
                return tree;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }
}
