using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.Core;

internal enum TestClassDeclarationOutcome
{
    Resolved,
    NoQualifyingClass,
    Unreadable,
    Unavailable
}

internal sealed record TestClassDeclarations(
    TestClassDeclarationOutcome Outcome,
    IReadOnlyList<string> ClassNames)
{
    internal static TestClassDeclarations Resolved(IEnumerable<string> classNames) =>
        new(
            TestClassDeclarationOutcome.Resolved,
            classNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    internal static TestClassDeclarations NoQualifyingClass { get; } =
        new(TestClassDeclarationOutcome.NoQualifyingClass, []);

    internal static TestClassDeclarations Unreadable { get; } =
        new(TestClassDeclarationOutcome.Unreadable, []);

    internal static TestClassDeclarations Unavailable { get; } =
        new(TestClassDeclarationOutcome.Unavailable, []);
}

internal enum ReverseDependencySelectionOutcome
{
    Resolved,
    Unreadable,
    Unavailable,
    Abandoned
}

internal enum ReverseDependencyCacheDisposition
{
    Hit,
    Miss,
    Bypass
}

internal sealed record ReverseDependencyCacheReceipt(
    ReverseDependencyCacheDisposition Disposition,
    string Fingerprint,
    int IndexedFileCount,
    long IndexedSourceBytes,
    int ReparsedFileCount,
    int RetainedSnapshotCount)
{
    internal string Render() =>
        $"reverse-dependency-cache={Disposition.ToString().ToLowerInvariant()} " +
        $"fingerprint={Fingerprint} indexed-files={IndexedFileCount} " +
        $"source-bytes={IndexedSourceBytes} reparsed-files={ReparsedFileCount} " +
        $"retained-snapshots={RetainedSnapshotCount}";
}

internal sealed record ReverseDependencyTestSelection(
    ReverseDependencySelectionOutcome Outcome,
    IReadOnlyList<string> TestClassNames,
    string? Reason,
    ReverseDependencyCacheReceipt? CacheReceipt = null)
{
    internal static ReverseDependencyTestSelection Resolved(
        IEnumerable<string> testClassNames,
        ReverseDependencyCacheReceipt? cacheReceipt = null) =>
        new(
            ReverseDependencySelectionOutcome.Resolved,
            testClassNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            null,
            cacheReceipt);

    internal static ReverseDependencyTestSelection Unreadable(
        string reason,
        ReverseDependencyCacheReceipt? cacheReceipt = null) =>
        new(ReverseDependencySelectionOutcome.Unreadable, [], reason, cacheReceipt);

    internal static ReverseDependencyTestSelection Abandoned(
        string reason,
        ReverseDependencyCacheReceipt? cacheReceipt = null) =>
        new(ReverseDependencySelectionOutcome.Abandoned, [], reason, cacheReceipt);

    internal static ReverseDependencyTestSelection Unavailable { get; } =
        new(ReverseDependencySelectionOutcome.Unavailable, [], "Reverse-dependency evidence is unavailable.");
}

internal interface ITestClassDeclarationReader
{
    TestClassDeclarations ReadFile(string repositoryRelativePath);

    TestClassDeclarations ReadProject(string repositoryRelativeDirectory);

    ReverseDependencyTestSelection ReadReverseDependentTestClasses(
        IReadOnlyList<string> changedSourcePaths) => ReverseDependencyTestSelection.Unavailable;
}

internal sealed class FileSystemTestClassDeclarationReader : ITestClassDeclarationReader
{
    private static readonly ConcurrentDictionary<ProjectCacheKey, TestClassDeclarations> ProjectCache = new();
    private readonly string _repositoryRoot;

    internal FileSystemTestClassDeclarationReader(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    internal static ITestClassDeclarationReader CreateForCurrentRepository()
    {
        foreach (var candidate in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(candidate));
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                    File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return new FileSystemTestClassDeclarationReader(directory.FullName);
                }

                directory = directory.Parent;
            }
        }

        return UnavailableTestClassDeclarationReader.Instance;
    }

    public TestClassDeclarations ReadFile(string repositoryRelativePath)
    {
        try
        {
            var path = ResolveWithinRepository(repositoryRelativePath);
            return path is null || !File.Exists(path)
                ? TestClassDeclarations.Unreadable
                : ReadDeclarations(path);
        }
        catch
        {
            return TestClassDeclarations.Unreadable;
        }
    }

    public TestClassDeclarations ReadProject(string repositoryRelativeDirectory)
    {
        try
        {
            var directory = ResolveWithinRepository(repositoryRelativeDirectory);
            if (directory is null || !Directory.Exists(directory))
            {
                return TestClassDeclarations.Unreadable;
            }

            var sourceFiles = EnumerateProjectSourceFiles(directory);
            var newestWrite = sourceFiles.Length == 0
                ? 0
                : sourceFiles.Max(path => File.GetLastWriteTimeUtc(path).Ticks);
            var cacheKey = new ProjectCacheKey(directory, sourceFiles.Length, newestWrite);
            return ProjectCache.GetOrAdd(cacheKey, _ => ReadProjectDeclarations(sourceFiles));
        }
        catch
        {
            return TestClassDeclarations.Unreadable;
        }
    }

    public ReverseDependencyTestSelection ReadReverseDependentTestClasses(
        IReadOnlyList<string> changedSourcePaths) =>
        ReverseDependencyTestImpactReader.Read(_repositoryRoot, changedSourcePaths);

    private TestClassDeclarations ReadProjectDeclarations(string[] sourceFiles)
    {
        var classNames = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            var declarations = ReadDeclarations(sourceFile);
            if (declarations.Outcome == TestClassDeclarationOutcome.Unreadable)
            {
                return TestClassDeclarations.Unreadable;
            }

            classNames.AddRange(declarations.ClassNames);
        }

        return classNames.Count == 0
            ? TestClassDeclarations.NoQualifyingClass
            : TestClassDeclarations.Resolved(classNames);
    }

    private static TestClassDeclarations ReadDeclarations(string path)
    {
        var source = File.ReadAllText(path);
        return CSharpTestClassScanner.TryReadClassNames(source, out var classNames)
            ? classNames.Count == 0
                ? TestClassDeclarations.NoQualifyingClass
                : TestClassDeclarations.Resolved(classNames)
            : TestClassDeclarations.Unreadable;
    }

    internal static string[] EnumerateProjectSourceFiles(string projectDirectory)
    {
        projectDirectory = Path.GetFullPath(projectDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var nestedProjectDirectories = Directory
            .EnumerateFiles(projectDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                !path.Equals(projectDirectory, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Directory
            .EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasGeneratedPathSegment(path))
            .Where(path => !nestedProjectDirectories.Any(directory => IsWithinDirectory(path, directory)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string? ResolveWithinRepository(string repositoryRelativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            _repositoryRoot,
            repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return IsWithinDirectory(fullPath, _repositoryRoot) ? fullPath : null;
    }

    private static bool HasGeneratedPathSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static bool IsWithinDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectCacheKey(string Directory, int FileCount, long NewestWriteTicks);
}

internal sealed class UnavailableTestClassDeclarationReader : ITestClassDeclarationReader
{
    internal static UnavailableTestClassDeclarationReader Instance { get; } = new();

    private UnavailableTestClassDeclarationReader()
    {
    }

    public TestClassDeclarations ReadFile(string repositoryRelativePath) => TestClassDeclarations.Unavailable;

    public TestClassDeclarations ReadProject(string repositoryRelativeDirectory) => TestClassDeclarations.Unavailable;
}

internal static class CSharpTestClassScanner
{
    internal sealed record SourceTypeDeclaration(
        string Name,
        bool IsPartial,
        IReadOnlySet<string> ReferencedIdentifiers);

    internal sealed record SourceSymbols(
        IReadOnlyList<SourceTypeDeclaration> DeclaredTypes,
        IReadOnlyList<string> TestClassNames);

    internal static bool TryReadClassNames(string source, out IReadOnlyList<string> classNames)
    {
        classNames = [];
        if (!TryReadSourceSymbols(source, out var symbols))
        {
            return false;
        }

        classNames = symbols.TestClassNames;
        return true;
    }

    internal static bool TryReadSourceSymbols(string source, out SourceSymbols symbols)
    {
        symbols = new SourceSymbols([], []);
        if (!TryTokenize(source, out var tokens) || !TryFindDeclarations(tokens, out var declarations))
        {
            return false;
        }

        var topLevelDeclarations = declarations
            .Where(declaration => !declarations.Any(parent =>
                parent.OpenBraceIndex < declaration.KeywordIndex &&
                declaration.KeywordIndex < parent.CloseBraceIndex))
            .ToArray();
        var classNames = topLevelDeclarations
            .Where(declaration => declaration.IsQualifyingClass)
            .Where(declaration => ContainsTestMethodAttribute(tokens, declaration, declarations))
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declarationNameIndexes = declarations
            .Select(declaration => declaration.NameIndex)
            .ToHashSet();
        var sourceTypeDeclarations = topLevelDeclarations
            .Select(declaration => new SourceTypeDeclaration(
                declaration.Name,
                declaration.IsPartial,
                ReadReferencedIdentifiers(tokens, declaration, declarationNameIndexes)))
            .OrderBy(declaration => declaration.Name, StringComparer.Ordinal)
            .ToArray();
        symbols = new SourceSymbols(
            sourceTypeDeclarations,
            classNames);
        return true;
    }

    private static IReadOnlySet<string> ReadReferencedIdentifiers(
        IReadOnlyList<Token> tokens,
        TypeDeclaration declaration,
        IReadOnlySet<int> declarationNameIndexes) =>
        tokens
            .Select((token, index) => (token, index))
            .Where(pair =>
                declaration.KeywordIndex <= pair.index &&
                pair.index <= declaration.CloseBraceIndex &&
                pair.token.IsIdentifier &&
                !declarationNameIndexes.Contains(pair.index))
            .Select(pair => pair.token.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static bool TryFindDeclarations(
        IReadOnlyList<Token> tokens,
        out IReadOnlyList<TypeDeclaration> declarations)
    {
        var found = new List<TypeDeclaration>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var keyword = tokens[index].Value;
            if (keyword is not ("class" or "record" or "struct" or "interface" or "enum"))
            {
                continue;
            }

            var nameIndex = index + 1;
            var isRecordStruct = keyword == "record" &&
                nameIndex < tokens.Count &&
                tokens[nameIndex].Value == "struct";
            var isRecordClass = keyword == "record" &&
                nameIndex < tokens.Count &&
                tokens[nameIndex].Value == "class";
            if (isRecordStruct || isRecordClass)
            {
                nameIndex++;
            }

            if (nameIndex >= tokens.Count || !tokens[nameIndex].IsIdentifier)
            {
                continue;
            }

            var openBraceIndex = FindBodyOpenBrace(tokens, nameIndex + 1);
            if (openBraceIndex < 0)
            {
                continue;
            }

            var closeBraceIndex = FindMatching(tokens, openBraceIndex, "{", "}");
            if (closeBraceIndex < 0)
            {
                declarations = [];
                return false;
            }

            var modifiers = ReadModifiers(tokens, index);
            found.Add(new TypeDeclaration(
                index,
                nameIndex,
                openBraceIndex,
                closeBraceIndex,
                tokens[nameIndex].Value,
                modifiers.Contains("partial"),
                IsQualifyingClass: keyword is "class" or "record" &&
                    !isRecordStruct &&
                    modifiers.Contains("public") &&
                    !modifiers.Contains("abstract") &&
                    !modifiers.Contains("static")));
        }

        declarations = found;
        return BracesAreBalanced(tokens);
    }

    private static int FindBodyOpenBrace(IReadOnlyList<Token> tokens, int startIndex)
    {
        for (var index = startIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Value == "{")
            {
                return index;
            }

            if (tokens[index].Value is ";" or "=>")
            {
                return -1;
            }
        }

        return -1;
    }

    private static HashSet<string> ReadModifiers(IReadOnlyList<Token> tokens, int keywordIndex)
    {
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = keywordIndex - 1; index >= 0; index--)
        {
            if (tokens[index].Value is "{" or "}" or ";")
            {
                break;
            }

            modifiers.Add(tokens[index].Value);
        }

        return modifiers;
    }

    private static bool ContainsTestMethodAttribute(
        IReadOnlyList<Token> tokens,
        TypeDeclaration declaration,
        IReadOnlyList<TypeDeclaration> declarations)
    {
        for (var index = declaration.OpenBraceIndex + 1; index < declaration.CloseBraceIndex; index++)
        {
            var nested = declarations.FirstOrDefault(candidate =>
                candidate.KeywordIndex > declaration.OpenBraceIndex &&
                candidate.KeywordIndex < declaration.CloseBraceIndex &&
                candidate.OpenBraceIndex <= index &&
                index <= candidate.CloseBraceIndex);
            if (nested is not null)
            {
                index = nested.CloseBraceIndex;
                continue;
            }

            if (tokens[index].Value != "[")
            {
                continue;
            }

            var closeBracket = FindMatching(tokens, index, "[", "]");
            if (closeBracket < 0 || closeBracket >= declaration.CloseBraceIndex)
            {
                return false;
            }

            for (var attributeIndex = index + 1; attributeIndex < closeBracket; attributeIndex++)
            {
                if (!tokens[attributeIndex].IsIdentifier)
                {
                    continue;
                }

                var name = tokens[attributeIndex].Value;
                if (name.EndsWith("Fact", StringComparison.Ordinal) ||
                    name.EndsWith("Theory", StringComparison.Ordinal) ||
                    name.EndsWith("FactAttribute", StringComparison.Ordinal) ||
                    name.EndsWith("TheoryAttribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            index = closeBracket;
        }

        return false;
    }

    private static bool BracesAreBalanced(IReadOnlyList<Token> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Value == "{")
            {
                depth++;
            }
            else if (token.Value == "}" && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static int FindMatching(
        IReadOnlyList<Token> tokens,
        int openIndex,
        string openToken,
        string closeToken)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index].Value == openToken)
            {
                depth++;
            }
            else if (tokens[index].Value == closeToken && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryTokenize(string source, out IReadOnlyList<Token> tokens)
    {
        var found = new List<Token>();
        for (var index = 0; index < source.Length;)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not ('\r' or '\n'))
                {
                    index++;
                }
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens = [];
                    return false;
                }

                index = end + 2;
                continue;
            }

            if (TrySkipStringOrCharacter(source, ref index, out var recognized))
            {
                continue;
            }

            if (recognized)
            {
                tokens = [];
                return false;
            }

            if (char.IsLetter(source[index]) || source[index] == '_')
            {
                var start = index++;
                while (index < source.Length &&
                    (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                {
                    index++;
                }

                found.Add(new Token(source[start..index], IsIdentifier: true));
                continue;
            }

            if (source[index] == '=' && index + 1 < source.Length && source[index + 1] == '>')
            {
                found.Add(new Token("=>", IsIdentifier: false));
                index += 2;
                continue;
            }

            found.Add(new Token(source[index].ToString(), IsIdentifier: false));
            index++;
        }

        tokens = found;
        return true;
    }

    private static bool TrySkipStringOrCharacter(string source, ref int index, out bool recognized)
    {
        recognized = false;
        var start = index;
        if (source[index] == '\'')
        {
            recognized = true;
            index++;
            while (index < source.Length)
            {
                if (source[index] == '\\')
                {
                    index += 2;
                }
                else if (source[index++] == '\'')
                {
                    return true;
                }
            }

            return false;
        }

        var prefixIndex = index;
        var verbatim = false;
        var interpolated = false;
        while (prefixIndex < source.Length && source[prefixIndex] == '$')
        {
            interpolated = true;
            prefixIndex++;
        }
        if (prefixIndex < source.Length && source[prefixIndex] == '@')
        {
            verbatim = true;
            prefixIndex++;
            if (prefixIndex < source.Length && source[prefixIndex] == '$')
            {
                interpolated = true;
                prefixIndex++;
            }
        }
        else if (prefixIndex == index && source[prefixIndex] == '@')
        {
            verbatim = true;
            prefixIndex++;
            if (prefixIndex < source.Length && source[prefixIndex] == '$')
            {
                interpolated = true;
                prefixIndex++;
            }
        }

        if (prefixIndex >= source.Length || source[prefixIndex] != '"')
        {
            return false;
        }

        recognized = true;
        var quoteCount = 0;
        while (prefixIndex + quoteCount < source.Length && source[prefixIndex + quoteCount] == '"')
        {
            quoteCount++;
        }

        if (quoteCount >= 3 && !verbatim)
        {
            index = prefixIndex + quoteCount;
            while (index < source.Length)
            {
                var closingQuotes = 0;
                while (index + closingQuotes < source.Length && source[index + closingQuotes] == '"')
                {
                    closingQuotes++;
                }
                if (closingQuotes >= quoteCount)
                {
                    index += quoteCount;
                    return true;
                }

                index++;
            }

            return false;
        }

        index = prefixIndex + 1;
        var interpolationDepth = 0;
        while (index < source.Length)
        {
            if (interpolated && interpolationDepth > 0)
            {
                if (TrySkipStringOrCharacter(source, ref index, out var nestedRecognized))
                {
                    continue;
                }

                if (nestedRecognized)
                {
                    index = start;
                    return false;
                }
            }

            if (interpolated && source[index] == '{')
            {
                if (interpolationDepth == 0 &&
                    index + 1 < source.Length && source[index + 1] == '{')
                {
                    index += 2;
                }
                else
                {
                    interpolationDepth++;
                    index++;
                }
            }
            else if (interpolated && interpolationDepth > 0 && source[index] == '}')
            {
                interpolationDepth--;
                index++;
            }
            else if (interpolationDepth == 0 && verbatim && source[index] == '"' &&
                index + 1 < source.Length && source[index + 1] == '"')
            {
                index += 2;
            }
            else if (interpolationDepth == 0 && !verbatim && source[index] == '\\')
            {
                index += 2;
            }
            else if (interpolationDepth == 0 && source[index] == '"')
            {
                index++;
                return true;
            }
            else
            {
                index++;
            }
        }

        index = start;
        return false;
    }

    private sealed record Token(string Value, bool IsIdentifier);

    private sealed record TypeDeclaration(
        int KeywordIndex,
        int NameIndex,
        int OpenBraceIndex,
        int CloseBraceIndex,
        string Name,
        bool IsPartial,
        bool IsQualifyingClass);
}
