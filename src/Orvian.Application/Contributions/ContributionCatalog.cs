using System.Collections.Immutable;
using Orvian.UI.Abstractions;

namespace Orvian.Application.Contributions;

public sealed class ContributionCatalog : IContributionCatalog
{
    private readonly object _gate = new();
    private readonly ImmutableHashSet<string> _coreRoutes;
    private ContributionSnapshot _snapshot = new([], [], []);

    public ContributionCatalog(IEnumerable<string>? coreRoutes = null)
    {
        _coreRoutes = (coreRoutes ?? ["/", "/activity", "/settings"])
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public ContributionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public ContributionRegistrationResult Register(ContributionBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        lock (_gate)
        {
            var errors = Validate(batch, _snapshot);
            if (errors.Count > 0)
            {
                return ContributionRegistrationResult.Failure(errors);
            }

            _snapshot = new ContributionSnapshot(
                [.. _snapshot.NavigationPages, .. batch.NavigationPages],
                [.. _snapshot.Commands, .. batch.Commands],
                [.. _snapshot.Settings, .. batch.Settings]);

            return ContributionRegistrationResult.Success;
        }
    }

    public void RemovePlugin(string pluginId)
    {
        ValidatePluginId(pluginId);

        lock (_gate)
        {
            _snapshot = new ContributionSnapshot(
                [.. _snapshot.NavigationPages.Where(item => !MatchesPlugin(item, pluginId))],
                [.. _snapshot.Commands.Where(item => !MatchesPlugin(item, pluginId))],
                [.. _snapshot.Settings.Where(item => !MatchesPlugin(item, pluginId))]);
        }
    }

    private List<ContributionRegistrationError> Validate(
        ContributionBatch batch,
        ContributionSnapshot current)
    {
        var errors = new List<ContributionRegistrationError>();

        if (string.IsNullOrWhiteSpace(batch.PluginId))
        {
            errors.Add(new("plugin.invalid_id", "Plugin ID is required."));
            return errors;
        }

        ValidateContributions(batch.PluginId, batch.NavigationPages, "navigation", errors);
        ValidateContributions(batch.PluginId, batch.Commands, "command", errors);
        ValidateContributions(batch.PluginId, batch.Settings, "settings", errors);

        AddDuplicateErrors(
            batch.NavigationPages.Select(item => item.Id),
            current.NavigationPages.Select(item => item.Id),
            "navigation.duplicate_id",
            errors);
        AddDuplicateErrors(
            batch.Commands.Select(item => item.Id),
            current.Commands.Select(item => item.Id),
            "command.duplicate_id",
            errors);
        AddDuplicateErrors(
            batch.Settings.Select(item => item.Id),
            current.Settings.Select(item => item.Id),
            "settings.duplicate_id",
            errors);

        var existingRoutes = current.NavigationPages
            .Select(item => item.Route)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var page in batch.NavigationPages)
        {
            if (page.FeatureId is not null &&
                string.IsNullOrWhiteSpace(page.FeatureId))
            {
                errors.Add(new(
                    "navigation.invalid_feature_id",
                    "Associated feature IDs cannot be empty.",
                    page.Id));
            }

            if (string.IsNullOrWhiteSpace(page.Route) || !page.Route.StartsWith("/", StringComparison.Ordinal))
            {
                errors.Add(new(
                    "navigation.invalid_route",
                    "Navigation routes must be absolute application routes.",
                    page.Id));
            }
            else if (_coreRoutes.Contains(page.Route))
            {
                errors.Add(new(
                    "navigation.core_route",
                    $"The route '{page.Route}' is reserved by Orvian.",
                    page.Id));
            }
            else if (!existingRoutes.Add(page.Route))
            {
                errors.Add(new(
                    "navigation.duplicate_route",
                    $"The route '{page.Route}' is already registered.",
                    page.Id));
            }
        }

        return errors;
    }

    private static void ValidateContributions<T>(
        string pluginId,
        IEnumerable<T> contributions,
        string kind,
        ICollection<ContributionRegistrationError> errors)
        where T : IPluginContribution
    {
        foreach (var contribution in contributions)
        {
            if (!string.Equals(contribution.PluginId, pluginId, StringComparison.Ordinal))
            {
                errors.Add(new(
                    $"{kind}.plugin_mismatch",
                    "Contribution ownership must match the registering plugin.",
                    contribution.Id));
            }

            if (string.IsNullOrWhiteSpace(contribution.Id))
            {
                errors.Add(new(
                    $"{kind}.invalid_id",
                    "Contribution ID is required."));
            }
        }
    }

    private static void AddDuplicateErrors(
        IEnumerable<string> proposedIds,
        IEnumerable<string> existingIds,
        string code,
        ICollection<ContributionRegistrationError> errors)
    {
        var seen = existingIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in proposedIds)
        {
            if (!seen.Add(id))
            {
                errors.Add(new(code, $"The contribution ID '{id}' is already registered.", id));
            }
        }
    }

    private static bool MatchesPlugin(IPluginContribution contribution, string pluginId) =>
        string.Equals(contribution.PluginId, pluginId, StringComparison.Ordinal);

    private static void ValidatePluginId(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin ID is required.", nameof(pluginId));
        }
    }
}
