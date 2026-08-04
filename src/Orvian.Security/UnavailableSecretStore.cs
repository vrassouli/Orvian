namespace Orvian.Security;

public sealed class UnavailableSecretStore : ISecretStore
{
    public Task StoreAsync(
        SecretDescriptor descriptor,
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new PlatformNotSupportedException(
            "Persistent credential storage is not available on this platform."));

    public Task<SecretValue?> RetrieveAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SecretValue?>(new PlatformNotSupportedException(
            "Persistent credential storage is not available on this platform."));

    public Task DeleteAsync(
        SecretReferenceId id,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new PlatformNotSupportedException(
            "Persistent credential storage is not available on this platform."));
}
