namespace Orvian.Security;

public sealed record SecretKey(
    string HostFingerprint,
    string UserName,
    string AuthenticationContext);

public interface ISecretStore
{
    Task StoreAsync(SecretKey key, ReadOnlyMemory<char> secret, CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<char>?> RetrieveAsync(SecretKey key, CancellationToken cancellationToken = default);
    Task DeleteAsync(SecretKey key, CancellationToken cancellationToken = default);
}
