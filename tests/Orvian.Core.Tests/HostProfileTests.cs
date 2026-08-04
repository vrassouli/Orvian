using Orvian.Core.Hosts;
using Xunit;

namespace Orvian.Core.Tests;

public sealed class HostProfileTests
{
    [Fact]
    public void Valid_profile_is_normalized_without_secret_material()
    {
        var now = DateTimeOffset.UtcNow;

        var result = HostProfile.Create(
            HostProfileId.New(),
            " Production ",
            "server.example.com",
            22,
            " operator ",
            HostAuthenticationMethod.Password,
            "secret-ref-1",
            ["linux", "Production", "production"],
            "Managed host",
            true,
            HostConnectionPreferences.Default,
            now,
            now);

        Assert.True(result.IsSuccess);
        var profile = Assert.IsType<HostProfile>(result.Profile);
        Assert.Equal("Production", profile.DisplayName);
        Assert.Equal("operator", profile.UserName);
        Assert.Equal(["linux", "Production"], profile.Tags.ToArray());
        Assert.Equal("secret-ref-1", profile.CredentialSecretReference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("server name")]
    [InlineData("server\nname")]
    [InlineData("../server")]
    public void Invalid_host_name_is_rejected(string hostName)
    {
        var result = Create(hostName: hostName);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Errors,
            error => error.Code.StartsWith("host_name.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Invalid_port_is_rejected(int port)
    {
        var result = Create(port: port);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == "port.range");
    }

    [Fact]
    public void Unbounded_reconnect_policy_is_rejected()
    {
        var result = Create(
            preferences: new HostConnectionPreferences(
                TimeSpan.FromSeconds(15),
                MaximumReconnectAttempts: int.MaxValue));

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Errors,
            error => error.Code == "reconnect_attempts.range");
    }

    [Fact]
    public void Plaintext_secret_has_no_profile_field()
    {
        var propertyNames = typeof(HostProfile)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Password", propertyNames);
        Assert.DoesNotContain("Passphrase", propertyNames);
        Assert.DoesNotContain("PrivateKey", propertyNames);
    }

    [Fact]
    public void PrivateKeyAuthenticationRequiresKeyPath()
    {
        var now = DateTimeOffset.UtcNow;

        var result = HostProfile.Create(
            HostProfileId.New(),
            "Server",
            "server.example.com",
            22,
            "operator",
            HostAuthenticationMethod.PrivateKey,
            null,
            [],
            null,
            true,
            HostConnectionPreferences.Default,
            now,
            now);

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Errors,
            error => error.Code == "private_key_path.required");
    }

    private static HostProfileValidationResult Create(
        string hostName = "server.example.com",
        int port = 22,
        HostConnectionPreferences? preferences = null)
    {
        var now = DateTimeOffset.UtcNow;
        return HostProfile.Create(
            HostProfileId.New(),
            "Server",
            hostName,
            port,
            "operator",
            HostAuthenticationMethod.Password,
            null,
            [],
            null,
            true,
            preferences,
            now,
            now);
    }
}
