internal sealed class SharedIsolatedDotnetRoot(Func<IsolatedDotnetRootFixture> factory)
{
    private readonly object _gate = new();
    private IsolatedDotnetRootFixture? _owner;
    private int _holders;

    internal int Holders
    {
        get
        {
            lock (_gate)
                return _holders;
        }
    }

    internal string Acquire()
    {
        lock (_gate)
        {
            _owner ??= factory();
            _holders++;
            return _owner.RootPath;
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            if (_holders == 0)
                return;

            if (--_holders == 0)
            {
                var owner = _owner;
                _owner = null;
                owner?.Dispose();
            }
        }
    }
}
