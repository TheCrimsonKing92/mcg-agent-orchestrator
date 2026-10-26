using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ArchUnitNET.Loader;
using Architecture = ArchUnitNET.Domain.Architecture;

// Parallel-safe: each ordered assembly identity publishes one strongly held model for the process lifetime.
internal sealed class SharedArchitectureCache
{
    private readonly Func<Assembly[], Architecture> builder;
    private readonly ConcurrentDictionary<string, Lazy<Architecture>> models = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StrongBox<int>> buildCounts = new(StringComparer.Ordinal);

    public static SharedArchitectureCache Process { get; } = new(
        assemblies => new ArchLoader().LoadAssemblies(assemblies).Build());

    public SharedArchitectureCache(Func<Assembly[], Architecture> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        this.builder = builder;
    }

    public Architecture GetOrBuild(params Assembly[] assemblies)
    {
        var reduced = Reduce(assemblies);
        var key = Key(reduced);
        return models.GetOrAdd(key, _ => new Lazy<Architecture>(() =>
        {
            var count = buildCounts.GetOrAdd(key, _ => new StrongBox<int>());
            Interlocked.Increment(ref count.Value);
            return builder(reduced);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public int BuildCount(params Assembly[] assemblies)
    {
        var key = Key(Reduce(assemblies));
        return buildCounts.TryGetValue(key, out var count) ? Volatile.Read(ref count.Value) : 0;
    }

    public static Architecture Get(params Assembly[] assemblies) => Process.GetOrBuild(assemblies);

    private static Assembly[] Reduce(Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var reduced = new List<Assembly>();
        foreach (var assembly in assemblies)
        {
            if (assembly?.FullName is not { } name)
                throw new ArgumentException("Each assembly must have a full name.", nameof(assemblies));
            if (names.Add(name)) reduced.Add(assembly);
        }
        return reduced.ToArray();
    }

    private static string Key(Assembly[] assemblies) => string.Join("\n", assemblies.Select(assembly => assembly.FullName));
}
