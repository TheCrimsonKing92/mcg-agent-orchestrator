/// <summary>
/// Declares that a nonparallel collection protects only state owned by one test
/// process. It does not require exclusion against separate acceptance processes.
/// Shared filesystem, service, or machine resources must not use this declaration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProcessLocalTestCollectionAttribute(string reason) : Attribute
{
    public string Reason { get; } = !string.IsNullOrWhiteSpace(reason)
        ? reason
        : throw new ArgumentException("Describe the process-local state being protected.", nameof(reason));
}
