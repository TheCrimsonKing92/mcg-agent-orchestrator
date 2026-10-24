namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoToolCacheEnvironment
{
    public static void Apply(IDictionary<string, string?> environment, string root)
    {
        root = Path.GetFullPath(root);
        environment["GOPATH"] = Path.Combine(root, "go");
        environment["GOMODCACHE"] = Path.Combine(root, "go", "pkg", "mod");
        environment["GOCACHE"] = Path.Combine(root, "go-build");
        environment["GOTOOLCHAIN"] = "local";
        environment["GOENV"] = "off";
        environment["GOFLAGS"] = "-modcacherw";
    }

    public static bool IsVariable(string name) =>
        name.Equals("GOPATH", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GOMODCACHE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GOCACHE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GOTOOLCHAIN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GOENV", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GOFLAGS", StringComparison.OrdinalIgnoreCase);
}
