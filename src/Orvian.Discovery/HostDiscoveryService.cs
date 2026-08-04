using System.Collections.Immutable;
using Orvian.Core.Commands;

namespace Orvian.Discovery;

public sealed class HostDiscoveryService : IHostDiscoveryService
{
    private readonly ICommandExecutor _commandExecutor;
    private readonly ImmutableArray<DiscoveryProbe> _probes;
    private readonly TimeProvider _timeProvider;

    public HostDiscoveryService(
        ICommandExecutor commandExecutor,
        IEnumerable<DiscoveryProbe>? probes = null,
        TimeProvider? timeProvider = null)
    {
        _commandExecutor = commandExecutor ??
            throw new ArgumentNullException(nameof(commandExecutor));
        _probes = [.. probes ?? StandardDiscoveryProbes.Create()];
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_probes.Select(probe => probe.Id).Distinct(StringComparer.Ordinal).Count() !=
            _probes.Length)
        {
            throw new ArgumentException("Discovery probe IDs must be unique.", nameof(probes));
        }
    }

    public async Task<DiscoverySnapshot> DiscoverAsync(
        DiscoveryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var startedAt = _timeProvider.GetUtcNow();
        var facts = ImmutableDictionary.CreateBuilder<string, DiscoveryFact>(
            StringComparer.Ordinal);
        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var outcomes = ImmutableArray.CreateBuilder<DiscoveryProbeOutcome>();

        foreach (var probe in _probes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new(
                    probe.Id,
                    DiscoveryProbeStatus.Cancelled,
                    "Discovery was cancelled."));
                break;
            }

            var operationId = Guid.NewGuid();
            var result = await _commandExecutor.ExecuteAsync(
                new CommandRequest
                {
                    OperationId = operationId,
                    HostProfileId = context.HostProfileId.ToString(),
                    ConnectionId = context.ConnectionId,
                    PluginId = context.PluginId,
                    PluginVersion = context.PluginVersion,
                    RequiredPermission = "command.read.execute",
                    Executable = probe.Executable,
                    Arguments = probe.Arguments,
                    Kind = CommandKind.ReadOnly,
                    InvocationSource = InvocationSource.Discovery,
                    OutputLogging = OutputLoggingMode.Redacted,
                    Timeout = probe.Timeout
                },
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                outcomes.Add(new(
                    probe.Id,
                    probe.IsOptional &&
                    result.Status == CommandStatus.Failed &&
                    result.FailureClassification ==
                        CommandFailureClassification.NonzeroExit
                        ? DiscoveryProbeStatus.Unsupported
                        : MapStatus(result.Status),
                    result.SafeFailureMessage ?? "Discovery probe command failed."));
                continue;
            }

            if (result.StandardOutput.IsTruncated)
            {
                outcomes.Add(new(
                    probe.Id,
                    DiscoveryProbeStatus.Truncated,
                    "Discovery probe output was truncated."));
                continue;
            }

            DiscoveryParseResult parsed;
            try
            {
                parsed = probe.Parse(result.StandardOutput.Content);
            }
            catch (Exception)
            {
                parsed = DiscoveryParseResult.Failure(
                    "Discovery probe returned malformed output.");
            }

            if (!parsed.IsSuccess)
            {
                outcomes.Add(new(
                    probe.Id,
                    DiscoveryProbeStatus.ParsingFailed,
                    parsed.SafeFailureMessage));
                continue;
            }

            var observedAt = _timeProvider.GetUtcNow();
            foreach (var fact in parsed.Facts)
            {
                if (string.Equals(
                        fact.Key,
                        "package.provider",
                        StringComparison.Ordinal) &&
                    facts.ContainsKey(fact.Key))
                {
                    continue;
                }

                facts[fact.Key] = new(fact.Key, fact.Value, probe.Id, observedAt);
            }

            capabilities.UnionWith(parsed.Capabilities);
            outcomes.Add(new(probe.Id, DiscoveryProbeStatus.Succeeded));
        }

        var operatingSystem = ResolveOperatingSystem(facts);
        return new(
            DiscoverySnapshotId.New(),
            context.HostProfileId,
            context.ConnectionId,
            startedAt,
            _timeProvider.GetUtcNow(),
            operatingSystem,
            facts.ToImmutable(),
            capabilities.ToImmutable(),
            outcomes.ToImmutable());
    }

    private static DiscoveryProbeStatus MapStatus(CommandStatus status) =>
        status switch
        {
            CommandStatus.Cancelled or CommandStatus.CancellationUncertain =>
                DiscoveryProbeStatus.Cancelled,
            CommandStatus.TimedOut => DiscoveryProbeStatus.TimedOut,
            _ => DiscoveryProbeStatus.CommandFailed
        };

    private static OperatingSystemFamily ResolveOperatingSystem(
        IReadOnlyDictionary<string, DiscoveryFact> facts)
    {
        if (!facts.TryGetValue("os.family", out var fact))
        {
            return OperatingSystemFamily.Unknown;
        }

        return fact.Value switch
        {
            "linux" => OperatingSystemFamily.Linux,
            "freebsd" => OperatingSystemFamily.FreeBsd,
            "macos" => OperatingSystemFamily.MacOs,
            _ => OperatingSystemFamily.Unknown
        };
    }
}
