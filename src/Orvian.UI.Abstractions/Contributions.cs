using System.Collections.Immutable;

namespace Orvian.UI.Abstractions;

public interface IPluginContribution
{
    string PluginId { get; }

    string Id { get; }
}

public sealed record NavigationPageContribution(
    string PluginId,
    string Id,
    string Route,
    string Title,
    string AccessibleName,
    string Icon,
    string ViewModelTypeName,
    ImmutableArray<string> RequiredCapabilities,
    int SortOrder = 0,
    string? FeatureId = null) : IPluginContribution;

public sealed record CommandContribution(
    string PluginId,
    string Id,
    string Title,
    string Description,
    string HandlerTypeName,
    ImmutableArray<string> RequiredCapabilities,
    ImmutableArray<string> RequiredPermissions) : IPluginContribution;

public sealed record SettingsContribution(
    string PluginId,
    string Id,
    string Title,
    string Description,
    string ViewModelTypeName,
    int SortOrder = 0) : IPluginContribution;

public sealed record ContributionBatch(
    string PluginId,
    ImmutableArray<NavigationPageContribution> NavigationPages,
    ImmutableArray<CommandContribution> Commands,
    ImmutableArray<SettingsContribution> Settings)
{
    public static ContributionBatch Empty(string pluginId) =>
        new(pluginId, [], [], []);
}

public sealed record ContributionRegistrationError(
    string Code,
    string Message,
    string? ContributionId = null);

public sealed record ContributionRegistrationResult(
    bool IsSuccess,
    ImmutableArray<ContributionRegistrationError> Errors)
{
    public static ContributionRegistrationResult Success { get; } = new(true, []);

    public static ContributionRegistrationResult Failure(
        IEnumerable<ContributionRegistrationError> errors) =>
        new(false, [.. errors]);
}

public sealed record ContributionSnapshot(
    ImmutableArray<NavigationPageContribution> NavigationPages,
    ImmutableArray<CommandContribution> Commands,
    ImmutableArray<SettingsContribution> Settings);

public interface IContributionCatalog
{
    ContributionSnapshot Snapshot { get; }

    ContributionRegistrationResult Register(ContributionBatch batch);

    void RemovePlugin(string pluginId);
}
