internal static class ChaosGateSeededRepositoryTemplate
{
    private static readonly Lazy<string> Template = new(
        CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void CopyTo(string destinationDirectory)
    {
        var source = Template.Value;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetRelativePath(source, file)));
        }
    }

    private static string CreateTemplate()
    {
        try
        {
            return ChaosGateTestBase.CreateSeededRepoByLaunchingGit();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Chaos gate seeded git repository template could not be created.", exception);
        }
    }
}
