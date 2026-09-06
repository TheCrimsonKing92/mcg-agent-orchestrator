using System.Text.Json;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class HermesExecutableIdentityTests
{
    [Xunit.Fact]
    public async Task NativeOutputIsAcceptedFromOsBoundCleanAnnotatedCheckout()
    {
        using var fixture = new HermesIdentityTestFixture();

        var receipt = await fixture.Verifier.VerifyAsync(
            fixture.ExecutablePath,
            fixture.NativeVersionOutput,
            string.Empty,
            0,
            versionJobExitConfirmed: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(fixture.Root), receipt.InstallRoot);
        Assert.Equal(fixture.ExecutablePath, receipt.ImagePath);
        Assert.Equal(fixture.Pin.Commit, receipt.HeadCommit);
        Assert.Equal(fixture.Pin.TagObject, receipt.TagObject);
        Assert.Equal(fixture.Pin.Commit, receipt.PeeledCommit);
        Assert.True(receipt.WorkingTreeClean);
    }

    [Xunit.Fact]
    public async Task WrongCommitRefuses()
    {
        using var fixture = new HermesIdentityTestFixture();
        var verifier = new GitHermesExecutableIdentityVerifier(fixture.Pin with { Commit = new string('0', 40) });

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.WrongCommit, error.Reason);
    }

    [Xunit.Fact]
    public async Task LightweightTagRefusesTagObjectOnlyIdentity()
    {
        using var fixture = new HermesIdentityTestFixture(annotatedTag: false);

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => fixture.Verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.TagNotAnnotated, error.Reason);
    }

    [Xunit.Fact]
    public async Task ExecutableAndReportedInstallationMismatchRefusesMisleadingText()
    {
        using var launched = new HermesIdentityTestFixture();
        using var reported = new HermesIdentityTestFixture();

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => launched.Verifier.VerifyAsync(
            launched.ExecutablePath, reported.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.InstallDirectoryTextMismatch, error.Reason);
    }

    [Xunit.Fact]
    public async Task ModifiedTrackedSourceRefuses()
    {
        using var fixture = new HermesIdentityTestFixture();
        File.AppendAllText(fixture.TrackedSourcePath, "modified");

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => fixture.Verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.WorkingTreeModified, error.Reason);
    }

    [Xunit.Fact]
    public async Task ImageOutsideGitCheckoutRefusesUnverifiableSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-hermes-no-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var image = Path.Combine(root, "hermes.exe");
        File.WriteAllText(image, "fixture");
        try
        {
            var error = await Assert.ThrowsAsync<HermesIdentityException>(() =>
                new GitHermesExecutableIdentityVerifier().VerifyAsync(
                    image, NativeOutput(root), string.Empty, 0, true, TestContext.Current.CancellationToken));

            Assert.Equal(HermesIdentityRefusal.NotGitCheckout, error.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task MissingExecutableRefusesBeforeReadingVersionText()
    {
        using var fixture = new HermesIdentityTestFixture();

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => fixture.Verifier.VerifyAsync(
            Path.Combine(fixture.Root, "missing.exe"), fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.ImagePathMissingOnDisk, error.Reason);
    }

    [Xunit.Fact]
    public async Task WrongTagObjectRefusesEvenWhenHeadAndPeelMatch()
    {
        using var fixture = new HermesIdentityTestFixture();
        var verifier = new GitHermesExecutableIdentityVerifier(fixture.Pin with { TagObject = new string('0', 40) });

        var error = await Assert.ThrowsAsync<HermesIdentityException>(() => verifier.VerifyAsync(
            fixture.ExecutablePath, fixture.NativeVersionOutput, string.Empty, 0, true, TestContext.Current.CancellationToken));

        Assert.Equal(HermesIdentityRefusal.TagObjectMismatch, error.Reason);
    }

    [Xunit.Fact]
    public void StaleReceiptCannotBeReused()
    {
        using var fixture = new HermesIdentityTestFixture();
        var now = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        var receipt = fixture.Receipt(now - TimeSpan.FromMinutes(2));

        var error = Assert.Throws<HermesIdentityException>(() =>
            GitHermesExecutableIdentityVerifier.ValidateReceipt(receipt, now, fixture.Pin));

        Assert.Equal(HermesIdentityRefusal.ReceiptStale, error.Reason);
    }

    [Xunit.Fact]
    public void DefaultPinMatchesCheckedTrialPolicy()
    {
        var repositoryRoot = RepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "config", "trials", "hermes-acp-v2026.8.27.json")));
        var pin = document.RootElement.GetProperty("pin");

        Assert.Equal(pin.GetProperty("release").GetString(), HermesPinnedIdentity.Default.Release);
        Assert.Equal(pin.GetProperty("tagObject").GetString(), HermesPinnedIdentity.Default.TagObject);
        Assert.Equal(pin.GetProperty("commit").GetString(), HermesPinnedIdentity.Default.Commit);
    }

    private static string NativeOutput(string installRoot) =>
        $"Hermes Agent v0.20.6 (2026.8.27){Environment.NewLine}" +
        $"Install directory: {installRoot}{Environment.NewLine}" +
        "Install method: git";

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
}

internal sealed class HermesIdentityTestFixture : IDisposable
{
    private const string Release = "v2026.8.27";

    public HermesIdentityTestFixture(bool annotatedTag = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "mcg-hermes-identity", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(Root, ".venv", "Scripts");
        Directory.CreateDirectory(scripts);
        ExecutablePath = Path.Combine(scripts, "hermes.exe");
        TrackedSourcePath = Path.Combine(Root, "hermes-source.py");
        File.WriteAllText(ExecutablePath, "fixture executable");
        File.WriteAllText(TrackedSourcePath, "fixture source");
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Hermes identity tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "fixture candidate");
        RunGit("tag", annotatedTag ? "-a" : "", Release, annotatedTag ? "-m" : "", annotatedTag ? "fixture release" : "");

        var commit = Git("rev-parse", "HEAD");
        var tagObject = Git("rev-parse", $"refs/tags/{Release}");
        Pin = new HermesPinnedIdentity(Release, tagObject, commit);
        Verifier = new GitHermesExecutableIdentityVerifier(Pin);
        NativeVersionOutput =
            $"Hermes Agent v0.20.6 (2026.8.27){Environment.NewLine}" +
            $"Install directory: {Root}{Environment.NewLine}" +
            $"Install method: git{Environment.NewLine}" +
            "Python: 3.12.14";
    }

    public string Root { get; }
    public string ExecutablePath { get; }
    public string TrackedSourcePath { get; }
    public HermesPinnedIdentity Pin { get; }
    public GitHermesExecutableIdentityVerifier Verifier { get; }
    public string NativeVersionOutput { get; }

    public HermesExecutableIdentityReceipt Receipt(DateTimeOffset verifiedAtUtc) => new(
        ExecutablePath, Root, Root, "git", Pin.Release, Pin.Commit, Pin.TagObject, Pin.Commit,
        "Hermes Agent v0.20.6 (2026.8.27)", true, verifiedAtUtc, NativeVersionOutput, string.Empty, 0, true);

    public void Dispose()
    {
        if (!Directory.Exists(Root))
            return;
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }

    private string Git(params string[] arguments)
    {
        var result = GitCli.Run(Root, 5_000, arguments.Where(argument => !string.IsNullOrEmpty(argument)).ToArray());
        if (!result.Succeeded || result.DrainTimedOut)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
        return result.Output.Trim();
    }

    private void RunGit(params string[] arguments) => _ = Git(arguments);
}
