namespace Orvian.Discovery;

public enum OperatingSystemFamily
{
    Unknown,
    Linux,
    FreeBsd,
    MacOs,
    NetworkDevice,
    Hypervisor
}

public sealed record RemoteHost(
    string Id,
    string DisplayName,
    string HostFingerprint,
    OperatingSystemFamily OperatingSystem,
    string? Distribution,
    string? Version,
    IReadOnlySet<string> Capabilities);

public sealed record DiscoveryResult(
    OperatingSystemFamily OperatingSystem,
    string? Distribution,
    string? Version,
    IReadOnlySet<string> Capabilities,
    IReadOnlyDictionary<string, string> Facts);

public interface IHostDiscoveryService
{
    Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
}
