using Orvian.Application.Connections;
using Orvian.Connections;
using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class SecretStoreConnectionCredentialProviderTests
{
    [Fact]
    public async Task SessionCredentialTakesPrecedenceOverRememberedCredential()
    {
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        var profile = Profile("secret-remembered");
        await secrets.StoreAsync(
            new(
                new(profile.CredentialSecretReference!),
                SecretKind.SshPassword,
                profile.Id,
                profile.DisplayName),
            "remembered".AsMemory());
        session.Store(profile.Id, "session".AsSpan());
        var provider = new SecretStoreConnectionCredentialProvider(secrets, session);

        using var authentication = await provider.GetAuthenticationAsync(profile);

        var password = Assert.IsType<PasswordConnectionAuthentication>(authentication);
        Assert.Equal("session"u8.ToArray(), password.Password.ToArray());
    }

    [Fact]
    public async Task RememberedCredentialIsUsedWhenSessionCredentialIsAbsent()
    {
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        var profile = Profile("secret-remembered");
        await secrets.StoreAsync(
            new(
                new(profile.CredentialSecretReference!),
                SecretKind.SshPassword,
                profile.Id,
                profile.DisplayName),
            "remembered".AsMemory());
        var provider = new SecretStoreConnectionCredentialProvider(secrets, session);

        using var authentication = await provider.GetAuthenticationAsync(profile);

        var password = Assert.IsType<PasswordConnectionAuthentication>(authentication);
        Assert.Equal("remembered"u8.ToArray(), password.Password.ToArray());
    }

    [Fact]
    public async Task MissingCredentialReturnsNull()
    {
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        var provider = new SecretStoreConnectionCredentialProvider(secrets, session);

        var authentication = await provider.GetAuthenticationAsync(Profile());

        Assert.Null(authentication);
    }

    [Fact]
    public async Task UnavailablePlatformStoreReturnsNull()
    {
        using var session = new SessionCredentialCache();
        var provider = new SecretStoreConnectionCredentialProvider(
            new UnavailableSecretStore(),
            session);

        var authentication = await provider.GetAuthenticationAsync(
            Profile("secret-unavailable"));

        Assert.Null(authentication);
    }

    [Fact]
    public async Task PrivateKeyProfileUsesPathAndRememberedPassphrase()
    {
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        var profile = PrivateKeyProfile("secret-passphrase");
        await secrets.StoreAsync(
            new(
                new(profile.CredentialSecretReference!),
                SecretKind.PrivateKeyPassphrase,
                profile.Id,
                profile.DisplayName),
            "key-passphrase".AsMemory());
        var provider = new SecretStoreConnectionCredentialProvider(secrets, session);

        using var authentication = await provider.GetAuthenticationAsync(profile);

        var privateKey = Assert.IsType<PrivateKeyConnectionAuthentication>(authentication);
        Assert.Equal("/keys/id_ed25519", privateKey.PrivateKeyPath);
        Assert.Equal("key-passphrase", privateKey.Passphrase.ToString());
    }

    [Fact]
    public async Task UnencryptedPrivateKeyDoesNotRequireStoredSecret()
    {
        using var secrets = new InMemorySecretStore();
        using var session = new SessionCredentialCache();
        var provider = new SecretStoreConnectionCredentialProvider(secrets, session);

        using var authentication = await provider.GetAuthenticationAsync(PrivateKeyProfile());

        Assert.IsType<PrivateKeyConnectionAuthentication>(authentication);
    }

    private static HostProfile Profile(string? secretReference = null) =>
        HostProfile.Create(
            HostProfileId.New(),
            "Server",
            "server.example",
            22,
            "admin",
            HostAuthenticationMethod.Password,
            secretReference,
            tags: null,
            notes: null,
            isEnabled: true,
            connectionPreferences: null,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow).Profile!;

    private static HostProfile PrivateKeyProfile(string? secretReference = null) =>
        HostProfile.Create(
            HostProfileId.New(),
            "Key server",
            "server.example",
            22,
            "admin",
            HostAuthenticationMethod.PrivateKey,
            secretReference,
            tags: null,
            notes: null,
            isEnabled: true,
            connectionPreferences: null,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow,
            privateKeyPath: "/keys/id_ed25519").Profile!;
}
