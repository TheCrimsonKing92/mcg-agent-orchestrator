internal sealed class ThrowingDisposable : IDisposable
{
    public void Dispose() => throw new InvalidOperationException("injected cleanup failure");
}
