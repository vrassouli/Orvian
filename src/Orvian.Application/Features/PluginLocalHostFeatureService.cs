using System.Collections.Immutable;
using Orvian.Application.Connections;
using Orvian.Application.Discovery;
using Orvian.Application.Hosts;
using Orvian.Core.Hosts;
using Orvian.Plugin.Abstractions;

namespace Orvian.Application.Features;

public sealed class PluginLocalHostFeatureService(
    IPluginFeatureCatalog features,
    IHostProfileRepository hostProfiles,
    IDiscoverySnapshotRepository discoverySnapshots,
    ITrustedHostKeyRepository trustedHostKeys,
    IConnectionSnapshotProvider connections,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool Supports(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        return features.LocalHostSnapshot.Any(item =>
            string.Equals(
                item.Provider.FeatureId,
                featureId,
                StringComparison.Ordinal));
    }

    public async Task<PluginFeatureReadResult> ReadAsync(
        string featureId,
        HostProfileId hostProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        var profile = await hostProfiles
            .GetAsync(hostProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return new(
                PluginFeatureReadStatus.Unsupported,
                SafeFailureMessage: "The selected host profile no longer exists.");
        }

        var discovery = await discoverySnapshots
            .GetLatestAsync(hostProfileId, cancellationToken)
            .ConfigureAwait(false);
        var trustedKey = await trustedHostKeys
            .GetAsync(hostProfileId, cancellationToken)
            .ConfigureAwait(false);
        var capabilities = discovery?.Capabilities ??
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        var registered = features.LocalHostSnapshot
            .Where(item =>
                string.Equals(
                    item.Provider.FeatureId,
                    featureId,
                    StringComparison.Ordinal) &&
                item.Provider.RequiredCapabilities.All(capabilities.Contains))
            .OrderByDescending(item => item.Provider.Priority)
            .ThenBy(item => item.Provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (registered is null)
        {
            return new(
                PluginFeatureReadStatus.Unsupported,
                SafeFailureMessage:
                    "No compatible local provider is available for this host.");
        }

        var facts = discovery?.Facts.ToImmutableDictionary(
            item => item.Key,
            item => item.Value.Value,
            StringComparer.Ordinal) ??
            ImmutableDictionary<string, string>.Empty.WithComparers(
                StringComparer.Ordinal);
        var context = new LocalHostFeatureContext(
            profile.DisplayName,
            $"{profile.HostName}:{profile.Port}",
            connections.GetSnapshot(hostProfileId).State.ToString(),
            trustedKey?.Algorithm,
            trustedKey?.Sha256Fingerprint,
            _timeProvider.GetUtcNow(),
            discovery?.CompletedAt,
            discovery?.IsPartial ?? false,
            discovery?.OperatingSystem.ToString() ?? "Unknown",
            facts,
            capabilities);

        try
        {
            var result = registered.Provider.Build(context);
            return result.IsSuccess
                ? new(
                    PluginFeatureReadStatus.Succeeded,
                    registered.Provider.ProviderId,
                    result.Model)
                : new(
                    PluginFeatureReadStatus.ParsingFailed,
                    registered.Provider.ProviderId,
                    SafeFailureMessage: result.SafeFailureMessage);
        }
        catch (Exception)
        {
            return new(
                PluginFeatureReadStatus.ParsingFailed,
                registered.Provider.ProviderId,
                SafeFailureMessage:
                    "The local feature provider could not build a safe view.");
        }
    }
}
