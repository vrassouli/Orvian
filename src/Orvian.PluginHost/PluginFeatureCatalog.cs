using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;

namespace Orvian.PluginHost;

public sealed class PluginFeatureCatalog : IPluginFeatureCatalog
{
    private readonly object _gate = new();
    private ImmutableArray<RegisteredLocalHostFeatureProvider> _localHostProviders = [];
    private ImmutableArray<RegisteredReadFeatureProvider> _providers = [];
    private ImmutableArray<RegisteredMutationFeatureProvider> _mutationProviders = [];

    public ImmutableArray<RegisteredLocalHostFeatureProvider> LocalHostSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _localHostProviders;
            }
        }
    }

    public ImmutableArray<RegisteredReadFeatureProvider> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _providers;
            }
        }
    }

    public ImmutableArray<RegisteredMutationFeatureProvider> MutationSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _mutationProviders;
            }
        }
    }

    public void Register(
        string pluginId,
        Version pluginVersion,
        IReadOnlySet<string> permissions,
        IEnumerable<IReadOnlyFeatureProvider> readProviders) =>
        Register(pluginId, pluginVersion, permissions, [], readProviders, []);

    public void Register(
        string pluginId,
        Version pluginVersion,
        IReadOnlySet<string> permissions,
        IEnumerable<IReadOnlyFeatureProvider> readProviders,
        IEnumerable<IMutationFeatureProvider> mutationProviders) =>
        Register(
            pluginId,
            pluginVersion,
            permissions,
            [],
            readProviders,
            mutationProviders);

    public void Register(
        string pluginId,
        Version pluginVersion,
        IReadOnlySet<string> permissions,
        IEnumerable<ILocalHostFeatureProvider> localHostProviders,
        IEnumerable<IReadOnlyFeatureProvider> readProviders,
        IEnumerable<IMutationFeatureProvider> mutationProviders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(pluginVersion);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(localHostProviders);
        ArgumentNullException.ThrowIfNull(readProviders);
        ArgumentNullException.ThrowIfNull(mutationProviders);
        var proposedLocal = localHostProviders.ToArray();
        var proposed = readProviders.ToArray();
        var proposedMutations = mutationProviders.ToArray();
        var duplicateLocal = proposedLocal
            .GroupBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateLocal is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate local host provider ID '{duplicateLocal.Key}'.");
        }

        var duplicate = proposed
            .GroupBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate feature provider ID '{duplicate.Key}'.");
        }

        var duplicateMutation = proposedMutations
            .GroupBy(provider => provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMutation is not null)
        {
            throw new InvalidOperationException(
                $"Duplicate mutation provider ID '{duplicateMutation.Key}'.");
        }

        lock (_gate)
        {
            var existingLocalIds = _localHostProviders
                .Select(provider => provider.Provider.ProviderId)
                .ToHashSet(StringComparer.Ordinal);
            if (proposedLocal.Any(provider =>
                    !existingLocalIds.Add(provider.ProviderId)))
            {
                throw new InvalidOperationException(
                    "Local host provider ID is already registered.");
            }

            var existingIds = _providers
                .Select(provider => provider.Provider.ProviderId)
                .ToHashSet(StringComparer.Ordinal);
            if (proposed.Any(provider => !existingIds.Add(provider.ProviderId)))
            {
                throw new InvalidOperationException(
                    "Feature provider ID is already registered.");
            }

            var existingMutationIds = _mutationProviders
                .Select(provider => provider.Provider.ProviderId)
                .ToHashSet(StringComparer.Ordinal);
            if (proposedMutations.Any(provider =>
                    !existingMutationIds.Add(provider.ProviderId)))
            {
                throw new InvalidOperationException(
                    "Mutation provider ID is already registered.");
            }

            var immutablePermissions =
                permissions.ToImmutableHashSet(StringComparer.Ordinal);
            _localHostProviders =
            [
                .. _localHostProviders,
                .. proposedLocal.Select(provider =>
                    new RegisteredLocalHostFeatureProvider(
                        pluginId,
                        pluginVersion,
                        immutablePermissions,
                        provider))
            ];
            _providers =
            [
                .. _providers,
                .. proposed.Select(provider =>
                    new RegisteredReadFeatureProvider(
                        pluginId,
                        pluginVersion,
                        immutablePermissions,
                        provider))
            ];
            _mutationProviders =
            [
                .. _mutationProviders,
                .. proposedMutations.Select(provider =>
                    new RegisteredMutationFeatureProvider(
                        pluginId,
                        pluginVersion,
                        immutablePermissions,
                        provider))
            ];
        }
    }

    public void RemovePlugin(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate)
        {
            _localHostProviders =
            [
                .. _localHostProviders.Where(provider =>
                    !string.Equals(
                        provider.PluginId,
                        pluginId,
                        StringComparison.Ordinal))
            ];
            _providers =
            [
                .. _providers.Where(provider =>
                    !string.Equals(
                        provider.PluginId,
                        pluginId,
                        StringComparison.Ordinal))
            ];
            _mutationProviders =
            [
                .. _mutationProviders.Where(provider =>
                    !string.Equals(
                        provider.PluginId,
                        pluginId,
                        StringComparison.Ordinal))
            ];
        }
    }
}
