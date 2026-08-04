using System.Collections.Immutable;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;

namespace Orvian.Discovery;

public readonly record struct DiscoverySnapshotId(Guid Value)
{
    public static DiscoverySnapshotId New() => new(Guid.NewGuid());
}

public enum OperatingSystemFamily
{
    Unknown,
    Linux,
    FreeBsd,
    MacOs
}

public enum DiscoveryProbeStatus
{
    Succeeded = 0,
    CommandFailed = 1,
    ParsingFailed = 2,
    Truncated = 3,
    Cancelled = 4,
    TimedOut = 5,
    Unsupported = 6
}

public sealed record DiscoveryFact(
    string Key,
    string Value,
    string ProbeId,
    DateTimeOffset ObservedAt);

public sealed record DiscoveryProbeOutcome(
    string ProbeId,
    DiscoveryProbeStatus Status,
    string? SafeFailureMessage = null);

public sealed record DiscoverySnapshot(
    DiscoverySnapshotId Id,
    HostProfileId HostProfileId,
    string ConnectionId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    OperatingSystemFamily OperatingSystem,
    ImmutableDictionary<string, DiscoveryFact> Facts,
    ImmutableHashSet<string> Capabilities,
    ImmutableArray<DiscoveryProbeOutcome> ProbeOutcomes)
{
    public bool IsPartial => ProbeOutcomes.Any(outcome =>
        outcome.Status is not (
            DiscoveryProbeStatus.Succeeded or
            DiscoveryProbeStatus.Unsupported));
}

public sealed record DiscoveryContext(
    HostProfileId HostProfileId,
    string ConnectionId,
    string PluginId,
    Version PluginVersion);

public interface IHostDiscoveryService
{
    Task<DiscoverySnapshot> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default);
}

public sealed record DiscoveryParseResult(
    ImmutableArray<KeyValuePair<string, string>> Facts,
    ImmutableArray<string> Capabilities,
    string? SafeFailureMessage = null)
{
    public bool IsSuccess => SafeFailureMessage is null;

    public static DiscoveryParseResult Failure(string safeMessage) =>
        new([], [], safeMessage);
}

public sealed record DiscoveryProbe(
    string Id,
    string Executable,
    ImmutableArray<CommandArgument> Arguments,
    TimeSpan Timeout,
    Func<string, DiscoveryParseResult> Parse,
    bool IsOptional = false);
