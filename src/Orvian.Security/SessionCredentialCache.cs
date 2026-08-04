using Orvian.Core.Hosts;

namespace Orvian.Security;

public interface ISessionCredentialCache
{
    void Store(HostProfileId hostProfileId, ReadOnlySpan<char> secret);

    SecretValue? Retrieve(HostProfileId hostProfileId);

    void Delete(HostProfileId hostProfileId);
}

public sealed class SessionCredentialCache : ISessionCredentialCache, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<HostProfileId, char[]> _credentials = [];
    private bool _disposed;

    public void Store(HostProfileId hostProfileId, ReadOnlySpan<char> secret)
    {
        Validate(hostProfileId);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_credentials.Remove(hostProfileId, out var previous))
            {
                Array.Clear(previous);
            }

            _credentials.Add(hostProfileId, secret.ToArray());
        }
    }

    public SecretValue? Retrieve(HostProfileId hostProfileId)
    {
        Validate(hostProfileId);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _credentials.TryGetValue(hostProfileId, out var secret)
                ? new SecretValue(secret)
                : null;
        }
    }

    public void Delete(HostProfileId hostProfileId)
    {
        Validate(hostProfileId);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_credentials.Remove(hostProfileId, out var secret))
            {
                Array.Clear(secret);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var secret in _credentials.Values)
            {
                Array.Clear(secret);
            }

            _credentials.Clear();
            _disposed = true;
        }
    }

    private static void Validate(HostProfileId hostProfileId)
    {
        if (hostProfileId.Value == Guid.Empty)
        {
            throw new ArgumentException("Host profile ID is required.", nameof(hostProfileId));
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
