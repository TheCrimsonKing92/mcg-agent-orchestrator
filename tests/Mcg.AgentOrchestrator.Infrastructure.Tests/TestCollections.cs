using Mcg.AgentOrchestrator.Infrastructure;

// Pins MCG_DOTNET_ISOLATED_ROOT to an ephemeral per-run root for the dotnet
// build-slot collection. Per-test EnvVarScope overrides nest cleanly on top of
// this baseline and the collection root is deleted only after the slot tests finish.
public sealed class IsolatedDotnetRootFixture : IDisposable
{
    private readonly string? _originalValue;
    private readonly string _root;

    public IsolatedDotnetRootFixture()
    {
        _originalValue = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(localAppData))
        {
            // The worker TEMP directory is nested under its sandbox and is too long for SDK native-copy
            // intermediates. Claim the compact random leaf atomically before creating its directory.
            _root = CreateShortWindowsRoot(localAppData);
        }
        else
        {
            _root = Directory.CreateTempSubdirectory("mdi-").FullName;
        }
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root);
    }

    private static string CreateShortWindowsRoot(string localAppData)
    {
        var basePath = Path.GetFullPath(Path.Combine(localAppData, "..", "LocalLow"));
        try
        {
            Directory.CreateDirectory(basePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Could not create the short isolated dotnet test root base '{basePath}'.",
                ex);
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = Path.Combine(basePath, $"mdi-{Guid.NewGuid():N}"[..20]);
            var claimPath = candidate + ".claim";
            FileStream claim;
            try
            {
                claim = new FileStream(claimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when (IsClaimCollision(ex))
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not atomically claim short isolated dotnet test root '{candidate}'.",
                    ex);
            }

            try
            {
                if (Directory.Exists(candidate))
                {
                    continue;
                }

                Directory.CreateDirectory(candidate);
                return candidate;
            }
            finally
            {
                claim.Dispose();
                File.Delete(claimPath);
            }
        }

        throw new IOException("Could not claim a short isolated dotnet test root.");
    }

    private static bool IsClaimCollision(IOException exception) =>
        (exception.HResult & 0xffff) is 80 or 183;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _originalValue);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
