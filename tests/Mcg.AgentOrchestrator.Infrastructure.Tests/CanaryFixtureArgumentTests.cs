using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

public sealed class CanaryFixtureArgumentTests
{
    private const string MissingValuePrefix =
        "Known-green canary is missing a required argument value: ";

    private static readonly Lazy<MethodInfo> FixtureEntryPoint = new(CompileFixtureEntryPoint);

    [Fact]
    public void CanonicalCanaryInvocation_WritesRequestedTrx()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var resultsDirectory = Path.Combine(tempRoot, "nested", "results");
            var trxFileName = $"post-landing-canary-{Guid.NewGuid():N}.trx";
            var expectedPath = Path.Combine(resultsDirectory, trxFileName);
            var invocation = InvokeFixture(
            [
                "--results-directory",
                resultsDirectory,
                "--report-trx",
                "--report-trx-filename",
                trxFileName
            ]);

            Assert.Equal(0, invocation.ExitCode);
            Assert.True(File.Exists(expectedPath), $"Expected TRX was not written to '{expectedPath}'.");
            var actualPath = Assert.Single(
                Directory.EnumerateFiles(tempRoot, "*.trx", SearchOption.AllDirectories));
            Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(actualPath));

            var document = XDocument.Load(expectedPath);
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var counters = Assert.Single(document.Descendants(trx + "Counters"));
            Assert.Equal("1", counters.Attribute("executed")?.Value);
            Assert.Equal("1", counters.Attribute("passed")?.Value);
            Assert.Equal("0", counters.Attribute("failed")?.Value);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("results-absent", "--results-directory")]
    [InlineData("filename-absent", "--report-trx-filename")]
    [InlineData("results-dangling", "--results-directory")]
    [InlineData("filename-dangling", "--report-trx-filename")]
    [InlineData("results-followed-by-option", "--results-directory")]
    [InlineData("filename-followed-by-option", "--report-trx-filename")]
    [InlineData("results-blank", "--results-directory")]
    [InlineData("filename-blank", "--report-trx-filename")]
    public void MissingRequiredValue_ReturnsNamedFailure(string caseName, string missingArgument)
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var invocation = InvokeFixture(CreateMissingValueArguments(caseName, tempRoot));

            Assert.Equal(2, invocation.ExitCode);
            Assert.Contains(
                MissingValuePrefix + missingArgument,
                invocation.StandardError,
                StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(tempRoot, "*.trx", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void UnrecognisedArguments_AreIgnored()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var resultsDirectory = Path.Combine(tempRoot, "results");
            const string trxFileName = "canary-with-extras.trx";
            var invocation = InvokeFixture(
            [
                "--future-leading-flag",
                "leading-value",
                "--results-directory",
                resultsDirectory,
                "--filter-class",
                "CanaryTests",
                "--report-trx",
                "--report-trx-filename",
                trxFileName,
                "--future-trailing-flag",
                "trailing-value"
            ]);

            Assert.Equal(0, invocation.ExitCode);
            Assert.True(File.Exists(Path.Combine(resultsDirectory, trxFileName)));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string[] CreateMissingValueArguments(string caseName, string tempRoot)
    {
        var resultsDirectory = Path.Combine(tempRoot, "results");
        return caseName switch
        {
            "results-absent" => ["--report-trx", "--report-trx-filename", "canary.trx"],
            "filename-absent" => ["--results-directory", resultsDirectory, "--report-trx"],
            "results-dangling" =>
                ["--report-trx-filename", "canary.trx", "--report-trx", "--results-directory"],
            "filename-dangling" =>
                ["--results-directory", resultsDirectory, "--report-trx", "--report-trx-filename"],
            "results-followed-by-option" =>
                ["--results-directory", "--report-trx", "--report-trx-filename", "canary.trx"],
            "filename-followed-by-option" =>
                ["--results-directory", resultsDirectory, "--report-trx-filename", "--report-trx"],
            "results-blank" =>
                ["--results-directory", " ", "--report-trx-filename", "canary.trx"],
            "filename-blank" =>
                ["--results-directory", resultsDirectory, "--report-trx-filename", "\t"],
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown test case.")
        };
    }

    private static FixtureInvocation InvokeFixture(string[] arguments)
    {
        var exitCode = -1;
        var standardError = string.Empty;
        _ = CaptureConsole(() =>
            standardError = CaptureConsoleError(() =>
                exitCode = (int)FixtureEntryPoint.Value.Invoke(null, new object?[] { arguments })!));
        return new FixtureInvocation(exitCode, standardError);
    }

    private static MethodInfo CompileFixtureEntryPoint()
    {
        var repositoryRoot = FindRepositoryRoot();
        var templatePath = Path.Combine(
            repositoryRoot,
            "tests",
            "canary-fixture",
            "tests",
            "Mcg.AgentOrchestrator.Core.Tests",
            "CanaryTests.cs.template");
        var implicitUsings = CSharpSyntaxTree.ParseText(
            """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """,
            new CSharpParseOptions(LanguageVersion.Latest));
        var template = CSharpSyntaxTree.ParseText(
            File.ReadAllText(templatePath),
            new CSharpParseOptions(LanguageVersion.Latest),
            templatePath);
        var trustedPlatformAssemblies =
            (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assemblies were unavailable.");
        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            $"CanaryFixtureArgumentTests_{Guid.NewGuid():N}",
            [implicitUsings, template],
            references,
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable));

        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        if (!emitResult.Success)
        {
            var diagnostics = string.Join(
                Environment.NewLine,
                emitResult.Diagnostics
                    .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString()));
            throw new InvalidOperationException($"Canary fixture compilation failed:{Environment.NewLine}{diagnostics}");
        }

        var assembly = Assembly.Load(assemblyStream.ToArray());
        return assembly.EntryPoint
            ?? throw new InvalidOperationException("Canary fixture compilation produced no entry point.");
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verifiedRoot))
            return verifiedRoot;

        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from source file path '{sourceFilePath}'.");
    }

    private sealed record FixtureInvocation(int ExitCode, string StandardError);
}
