namespace Orvian.Security;

public sealed class InMemorySecretStore : ISecretStore, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<SecretReferenceId, StoredSecret> _secrets = [];
    private bool _disposed;

    public Task StoreAsync(
        SecretDescriptor descriptor,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        SecretStoreValidation.Validate(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_secrets.Remove(descriptor.Id, out var previous))
            {
                Array.Clear(previous.Value);
            }

            _secrets.Add(descriptor.Id, new(descriptor, secret.ToArray()));
        }

        return Task.CompletedTask;
    }

    public Task<SecretValue?> RetrieveAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(id);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_secrets.TryGetValue(id, out var secret))
            {
                return Task.FromResult<SecretValue?>(null);
            }

            return Task.FromResult<SecretValue?>(new SecretValue(secret.Value));
        }
    }

    public Task DeleteAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(id);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_secrets.Remove(id, out var secret))
            {
                Array.Clear(secret.Value);
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var secret in _secrets.Values)
            {
                Array.Clear(secret.Value);
            }

            _secrets.Clear();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record StoredSecret(SecretDescriptor Descriptor, char[] Value);
}
