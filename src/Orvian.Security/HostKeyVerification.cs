using System.Net;
using Orvian.Core.Hosts;

namespace Orvian.Security;

public enum HostKeyDecision
{
    Unknown,
    TrustedMatch,
    Changed,
    AssociationChanged
}

public sealed record HostKeyVerificationResult(
    HostKeyDecision Decision,
    TrustedHostKey? Previous,
    HostKeyObservation Observed)
{
    public bool MayContinueWithoutPrompt => Decision == HostKeyDecision.TrustedMatch;
}

public static class HostKeyVerifier
{
    public static HostKeyVerificationResult Verify(
        TrustedHostKey? trusted,
        HostKeyObservation observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        Validate(observed);

        if (trusted is null)
        {
            return new(HostKeyDecision.Unknown, null, observed);
        }

        if (trusted.HostProfileId != observed.HostProfileId ||
            !string.Equals(trusted.HostName, observed.HostName, StringComparison.OrdinalIgnoreCase) ||
            trusted.Port != observed.Port ||
            !string.Equals(
                trusted.ResolvedAddress,
                observed.ResolvedAddress,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(HostKeyDecision.AssociationChanged, trusted, observed);
        }

        if (!string.Equals(
                trusted.Algorithm,
                observed.Algorithm,
                StringComparison.Ordinal) ||
            !string.Equals(
                trusted.Sha256Fingerprint,
                observed.Sha256Fingerprint,
                StringComparison.Ordinal))
        {
            return new(HostKeyDecision.Changed, trusted, observed);
        }

        return new(HostKeyDecision.TrustedMatch, trusted, observed);
    }

    public static TrustedHostKey Trust(
        HostKeyObservation observed,
        DateTimeOffset trustedAt)
    {
        Validate(observed);
        return new(
            observed.HostProfileId,
            observed.HostName,
            observed.Port,
            observed.Algorithm,
            observed.Sha256Fingerprint,
            trustedAt,
            observed.ResolvedAddress);
    }

    private static void Validate(HostKeyObservation observation)
    {
        if (observation.HostProfileId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(observation.HostName) ||
            observation.Port is < 1 or > 65535 ||
            (observation.ResolvedAddress is not null &&
             !IPAddress.TryParse(observation.ResolvedAddress, out _)) ||
            string.IsNullOrWhiteSpace(observation.Algorithm) ||
            string.IsNullOrWhiteSpace(observation.Sha256Fingerprint) ||
            !observation.Sha256Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Host-key observation is invalid.", nameof(observation));
        }
    }
}
