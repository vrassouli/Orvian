using System.Text;
using Orvian.Connections;
using Orvian.Core.Hosts;
using Orvian.Security;

namespace Orvian.Application.Connections;

public sealed class SecretStoreConnectionCredentialProvider : IConnectionCredentialProvider
{
    private readonly ISecretStore _secretStore;
    private readonly ISessionCredentialCache _sessionCredentials;

    public SecretStoreConnectionCredentialProvider(
        ISecretStore secretStore,
        ISessionCredentialCache sessionCredentials)
    {
        _secretStore = secretStore;
        _sessionCredentials = sessionCredentials;
    }

    public async Task<ConnectionAuthentication?> GetAuthenticationAsync(
        HostProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        using var sessionSecret = _sessionCredentials.Retrieve(profile.Id);
        if (sessionSecret is not null)
        {
            return CreateAuthentication(profile, sessionSecret.Memory.Span);
        }

        if (profile.CredentialSecretReference is null)
        {
            return profile.AuthenticationMethod == HostAuthenticationMethod.PrivateKey &&
                profile.PrivateKeyPath is not null
                ? new PrivateKeyConnectionAuthentication(
                    profile.PrivateKeyPath,
                    ReadOnlySpan<char>.Empty)
                : null;
        }

        SecretValue? storedSecret;
        try
        {
            storedSecret = await _secretStore
                .RetrieveAsync(
                    new SecretReferenceId(profile.CredentialSecretReference),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SecretStoreException or PlatformNotSupportedException)
        {
            return null;
        }

        using (storedSecret)
        {
            return storedSecret is null
                ? null
                : CreateAuthentication(profile, storedSecret.Memory.Span);
        }
    }

    private static ConnectionAuthentication? CreateAuthentication(
        HostProfile profile,
        ReadOnlySpan<char> secret)
    {
        if (profile.AuthenticationMethod == HostAuthenticationMethod.PrivateKey)
        {
            return profile.PrivateKeyPath is null
                ? null
                : new PrivateKeyConnectionAuthentication(profile.PrivateKeyPath, secret);
        }

        if (profile.AuthenticationMethod != HostAuthenticationMethod.Password)
        {
            return null;
        }

        var bytes = new byte[Encoding.UTF8.GetByteCount(secret)];
        try
        {
            Encoding.UTF8.GetBytes(secret, bytes);
            return new PasswordConnectionAuthentication(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }
}
