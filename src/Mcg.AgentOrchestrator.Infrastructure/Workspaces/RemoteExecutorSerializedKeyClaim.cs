using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Only the open handles convey ownership; persistent empty files are safe after a crash.
internal static class RemoteExecutorSerializedKeyClaim
{
    internal static IDisposable? TryClaim(string root, string executor, IReadOnlyList<string> keys,
        out string? busyKey)
    {
        busyKey = null;
        if (string.IsNullOrWhiteSpace(executor) || executor is "." or ".." ||
            executor.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var claims = new Claims();
        foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            try
            {
                var folder = RemoteExecutorOccupancy.Folder(root, executor);
                Directory.CreateDirectory(folder);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16];
                claims.Add(new FileStream(Path.Combine(folder, $"key-{hash}.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                claims.Dispose();
                busyKey = key;
                return null;
            }
        }
        return claims;
    }

    private sealed class Claims : IDisposable
    {
        private readonly List<IDisposable> _handles = [];
        internal void Add(IDisposable handle) => _handles.Add(handle);
        public void Dispose()
        {
            foreach (var handle in _handles) handle.Dispose();
            _handles.Clear();
        }
    }
}
