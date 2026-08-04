using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class HostKeyVerifierTests
{
    [Fact]
    public void First_seen_key_requires_explicit_decision()
    {
        var result = HostKeyVerifier.Verify(null, Observation());

        Assert.Equal(HostKeyDecision.Unknown, result.Decision);
        Assert.False(result.MayContinueWithoutPrompt);
    }

    [Fact]
    public void Exact_trusted_key_may_continue()
    {
        var observation = Observation();
        var trusted = HostKeyVerifier.Trust(observation, DateTimeOffset.UtcNow);

        var result = HostKeyVerifier.Verify(trusted, observation);

        Assert.Equal(HostKeyDecision.TrustedMatch, result.Decision);
        Assert.True(result.MayContinueWithoutPrompt);
    }

    [Theory]
    [InlineData("SHA256:changed", "ssh-ed25519")]
    [InlineData("SHA256:known", "ecdsa-sha2-nistp256")]
    public void Changed_key_or_algorithm_blocks_by_default(
        string fingerprint,
        string algorithm)
    {
        var observation = Observation();
        var trusted = HostKeyVerifier.Trust(observation, DateTimeOffset.UtcNow);

        var result = HostKeyVerifier.Verify(
            trusted,
            observation with
            {
                Sha256Fingerprint = fingerprint,
                Algorithm = algorithm
            });

        Assert.Equal(HostKeyDecision.Changed, result.Decision);
        Assert.False(result.MayContinueWithoutPrompt);
    }

    [Fact]
    public void Hostname_change_requires_re_evaluation_even_with_same_key()
    {
        var observation = Observation();
        var trusted = HostKeyVerifier.Trust(observation, DateTimeOffset.UtcNow);

        var result = HostKeyVerifier.Verify(
            trusted,
            observation with { HostName = "other.example.test" });

        Assert.Equal(HostKeyDecision.AssociationChanged, result.Decision);
        Assert.False(result.MayContinueWithoutPrompt);
    }

    [Fact]
    public void Resolved_address_change_requires_re_evaluation_even_with_same_key()
    {
        var observation = Observation();
        var trusted = HostKeyVerifier.Trust(observation, DateTimeOffset.UtcNow);

        var result = HostKeyVerifier.Verify(
            trusted,
            observation with { ResolvedAddress = "192.0.2.11" });

        Assert.Equal(HostKeyDecision.AssociationChanged, result.Decision);
        Assert.False(result.MayContinueWithoutPrompt);
    }

    [Fact]
    public void Legacy_record_without_address_requires_one_time_revalidation()
    {
        var observation = Observation();
        var trusted = new TrustedHostKey(
            observation.HostProfileId,
            observation.HostName,
            observation.Port,
            observation.Algorithm,
            observation.Sha256Fingerprint,
            DateTimeOffset.UtcNow);

        var result = HostKeyVerifier.Verify(trusted, observation);

        Assert.Equal(HostKeyDecision.AssociationChanged, result.Decision);
        Assert.False(result.MayContinueWithoutPrompt);
    }

    [Fact]
    public void Invalid_resolved_address_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => HostKeyVerifier.Verify(
                null,
                Observation() with { ResolvedAddress = "not-an-ip-address" }));
    }

    private static HostKeyObservation Observation() =>
        new(
            HostProfileId.New(),
            "server.example.test",
            22,
            "192.0.2.10",
            "ssh-ed25519",
            "SHA256:known");
}
