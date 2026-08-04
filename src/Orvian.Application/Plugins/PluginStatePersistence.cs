using System.Collections.Immutable;

namespace Orvian.Application.Plugins;

public sealed record PersistedPluginDiagnostic(string Code, string Message);

public sealed record PersistedPluginState(
    string PluginId,
    string ObservedVersion,
    bool IsEnabled,
    string LifecycleState,
    ImmutableArray<PersistedPluginDiagnostic> Diagnostics,
    DateTimeOffset UpdatedAt);

public interface IPluginStateRepository
{
    Task<PersistedPluginState?> GetAsync(
        string pluginId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        PersistedPluginState state,
        CancellationToken cancellationToken = default);
}

public sealed record PluginManagementItem(
    string PluginId,
    string Name,
    string Publisher,
    string Version,
    string LifecycleState,
    bool IsEnabled,
    ImmutableArray<string> Permissions,
    ImmutableArray<PersistedPluginDiagnostic> Diagnostics);

public interface IPluginManagementService
{
    Task<IReadOnlyList<PluginManagementItem>> GetPluginsAsync(
        CancellationToken cancellationToken = default);

    Task<PluginManagementItem> SetEnabledAsync(
        string pluginId,
        bool isEnabled,
        CancellationToken cancellationToken = default);
}

public sealed class PluginStateChangeException(string safeMessage)
    : InvalidOperationException(safeMessage)
{
    public string SafeMessage { get; } = safeMessage;
}
