namespace Orvian.Core.Hosts;

public sealed record HostKeyObservation(
    HostProfileId HostProfileId,
    string HostName,
    int Port,
    string? ResolvedAddress,
    string Algorithm,
    string Sha256Fingerprint);

public sealed record TrustedHostKey(
    HostProfileId HostProfileId,
    string HostName,
    int Port,
    string Algorithm,
    string Sha256Fingerprint,
    DateTimeOffset TrustedAt,
    string? ResolvedAddress = null);
