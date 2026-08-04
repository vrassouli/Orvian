using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Orvian.Application.Plugins;
using Orvian.Plugin.Abstractions;
using Orvian.UI.Abstractions;

namespace Orvian.PluginHost;

public sealed class PluginHostService : IAsyncDisposable, IPluginManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly IContributionCatalog _contributionCatalog;
    private readonly PluginManifestValidator _validator;
    private readonly IPluginFeatureCatalog _featureCatalog;
    private readonly IPluginStateRepository? _stateRepository;
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiscoveredPlugin> _discovered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginManagementItem> _inventory =
        new(StringComparer.Ordinal);

    public PluginHostService(
        IContributionCatalog contributionCatalog,
        Version supportedApiVersion,
        IPluginFeatureCatalog? featureCatalog = null,
        IPluginStateRepository? stateRepository = null)
    {
        _contributionCatalog = contributionCatalog ??
            throw new ArgumentNullException(nameof(contributionCatalog));
        _validator = new PluginManifestValidator(supportedApiVersion);
        _featureCatalog = featureCatalog ?? new PluginFeatureCatalog();
        _stateRepository = stateRepository;
    }

    public IPluginFeatureCatalog Features => _featureCatalog;

    public Task<IReadOnlyList<PluginManagementItem>> GetPluginsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PluginManagementItem> snapshot =
            [.. _inventory.Values.OrderBy(item => item.Name, StringComparer.Ordinal)];
        return Task.FromResult(snapshot);
    }

    public async Task<PluginManagementItem> SetEnabledAsync(
        string pluginId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (!_inventory.ContainsKey(pluginId))
        {
            throw new PluginStateChangeException(
                "The plugin is no longer present in the installed inventory.");
        }

        if (isEnabled)
        {
            await EnableAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DisableAsync(pluginId, cancellationToken).ConfigureAwait(false);
            UpdateInventory(pluginId, PluginState.Disabled, [], isEnabled: false);
        }

        return _inventory[pluginId];
    }

    public async Task<IReadOnlyList<PluginLoadResult>> LoadDirectoryAsync(
        string pluginsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        if (!Directory.Exists(pluginsDirectory))
        {
            return [];
        }

        var scans = new List<ManifestScan>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     pluginsDirectory,
                     "orvian.plugin.json",
                     SearchOption.AllDirectories)
                 .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scans.Add(new(
                manifestPath,
                await ReadManifestDocumentAsync(manifestPath, cancellationToken)
                    .ConfigureAwait(false)));
        }

        var canonical = new Dictionary<string, ManifestScan>(StringComparer.Ordinal);
        foreach (var scan in scans)
        {
            if (scan.Document is { Id.Length: > 0 } document)
            {
                canonical.TryAdd(document.Id, scan);
            }
        }

        var (orderedIds, cyclicIds) = ResolveDependencyOrder(canonical);
        var orderedScans = orderedIds.Select(id => canonical[id]).ToList();
        orderedScans.AddRange(scans.Where(scan =>
            scan.Document is null ||
            string.IsNullOrEmpty(scan.Document.Id) ||
            !ReferenceEquals(canonical[scan.Document.Id], scan)));

        var results = new List<PluginLoadResult>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var canonicalResults = new Dictionary<string, PluginLoadResult>(
            StringComparer.Ordinal);
        foreach (var scan in orderedScans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = scan.Document;
            var isCanonical = document is { Id.Length: > 0 } &&
                              ReferenceEquals(canonical[document.Id], scan);
            PluginLoadResult result;
            if (isCanonical &&
                _validator.Validate(
                    document!,
                    Path.GetDirectoryName(scan.ManifestPath)!).IsEmpty)
            {
                var dependencyDiagnostic = GetDependencyDiagnostic(
                    document!,
                    canonical,
                    cyclicIds,
                    canonicalResults);
                if (dependencyDiagnostic is not null)
                {
                    seenIds.Add(document!.Id);
                    result = await RecordIncompatibleAsync(
                        scan.ManifestPath,
                        document,
                        dependencyDiagnostic,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await LoadManifestAsync(
                        scan.ManifestPath,
                        seenIds,
                        cancellationToken).ConfigureAwait(false);
                }

                canonicalResults[document!.Id] = result;
            }
            else
            {
                result = await LoadManifestAsync(
                    scan.ManifestPath,
                    seenIds,
                    cancellationToken).ConfigureAwait(false);
            }

            results.Add(result);
        }

        return results;
    }

    private static PluginDiagnostic? GetDependencyDiagnostic(
        PluginManifestDocument document,
        IReadOnlyDictionary<string, ManifestScan> canonical,
        IReadOnlySet<string> cyclicIds,
        IReadOnlyDictionary<string, PluginLoadResult> results)
    {
        if (cyclicIds.Contains(document.Id))
        {
            return new(
                "plugin.dependency_cycle",
                "Plugin belongs to a circular dependency graph.");
        }

        foreach (var dependency in document.Dependencies)
        {
            if (!canonical.ContainsKey(dependency))
            {
                return new(
                    "plugin.dependency_missing",
                    $"Required plugin dependency '{dependency}' is not installed.");
            }

            if (!results.TryGetValue(dependency, out var result) ||
                result.State != PluginState.Active)
            {
                return new(
                    "plugin.dependency_unavailable",
                    $"Required plugin dependency '{dependency}' is not active.");
            }
        }

        return null;
    }

    private static (
        IReadOnlyList<string> OrderedIds,
        IReadOnlySet<string> CyclicIds) ResolveDependencyOrder(
        IReadOnlyDictionary<string, ManifestScan> canonical)
    {
        var ordered = new List<string>();
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var cyclic = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string pluginId)
        {
            if (states.TryGetValue(pluginId, out var state))
            {
                if (state == 1)
                {
                    var cycleStart = stack.IndexOf(pluginId);
                    for (var index = cycleStart; index < stack.Count; index++)
                    {
                        cyclic.Add(stack[index]);
                    }
                }

                return;
            }

            states[pluginId] = 1;
            stack.Add(pluginId);
            foreach (var dependency in canonical[pluginId].Document!.Dependencies)
            {
                if (canonical.ContainsKey(dependency))
                {
                    Visit(dependency);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            states[pluginId] = 2;
            ordered.Add(pluginId);
        }

        foreach (var pluginId in canonical.Keys.Order(StringComparer.Ordinal))
        {
            Visit(pluginId);
        }

        return (ordered, cyclic);
    }

    private static async Task<PluginManifestDocument?> ReadManifestDocumentAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            return await JsonSerializer.DeserializeAsync<PluginManifestDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<PluginLoadResult> RecordIncompatibleAsync(
        string manifestPath,
        PluginManifestDocument document,
        PluginDiagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        _discovered[document.Id] = new(manifestPath, document);
        var persisted = _stateRepository is null
            ? null
            : await _stateRepository.GetAsync(document.Id, cancellationToken)
                .ConfigureAwait(false);
        var isEnabled = persisted?.IsEnabled ?? true;
        var result = new PluginLoadResult(
            document.Id,
            PluginState.Incompatible,
            [diagnostic]);
        await PersistStateAsync(
            document,
            isEnabled,
            result.State,
            result.Diagnostics,
            cancellationToken).ConfigureAwait(false);
        UpdateInventory(document.Id, result.State, result.Diagnostics, isEnabled);
        return result;
    }

    public async Task DisableAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var activeDependents = _discovered.Values
            .Where(item =>
                item.Document.Dependencies.Contains(pluginId, StringComparer.Ordinal) &&
                _loaded.ContainsKey(item.Document.Id))
            .Select(item => item.Document.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (activeDependents.Length > 0)
        {
            throw new PluginStateChangeException(
                $"Disable dependent plugins first: {string.Join(", ", activeDependents)}.");
        }

        if (_stateRepository is not null &&
            _discovered.TryGetValue(pluginId, out var discovered))
        {
            await PersistStateAsync(
                discovered.Document,
                isEnabled: false,
                PluginState.Disabled,
                [],
                cancellationToken).ConfigureAwait(false);
        }

        await UnloadAsync(pluginId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PluginLoadResult> EnableAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        if (_loaded.ContainsKey(pluginId))
        {
            return new(pluginId, PluginState.Active, []);
        }

        if (!_discovered.TryGetValue(pluginId, out var discovered))
        {
            throw new PluginStateChangeException(
                "The plugin is no longer present in the installed inventory.");
        }

        foreach (var dependency in discovered.Document.Dependencies)
        {
            if (!_loaded.ContainsKey(dependency))
            {
                throw new PluginStateChangeException(
                    $"Enable required plugin '{dependency}' first.");
            }
        }

        var result = await LoadValidatedManifestAsync(
            discovered.ManifestPath,
            discovered.Document,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistStateAsync(
                discovered.Document,
                isEnabled: true,
                result.State,
                result.Diagnostics,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await UnloadAsync(pluginId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        UpdateInventory(pluginId, result.State, result.Diagnostics, isEnabled: true);
        return result;
    }

    private async Task UnloadAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (!_loaded.Remove(pluginId, out var loaded))
        {
            return;
        }

        try
        {
            if (loaded.Instance is IAsyncPluginLifecycle lifecycle)
            {
                await lifecycle.DeactivateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _contributionCatalog.RemovePlugin(pluginId);
            _featureCatalog.RemovePlugin(pluginId);
            if (loaded.Instance is IDisposable disposable)
            {
                disposable.Dispose();
            }

            loaded.LoadContext.Unload();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pluginId in _loaded.Keys.ToArray())
        {
            await UnloadAsync(pluginId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<PluginLoadResult> LoadManifestAsync(
        string manifestPath,
        ISet<string> seenIds,
        CancellationToken cancellationToken)
    {
        PluginManifestDocument? document;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            document = await JsonSerializer.DeserializeAsync<PluginManifestDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Quarantined(
                Path.GetFileName(Path.GetDirectoryName(manifestPath)) ?? "unknown",
                "manifest.read_failed",
                "Plugin manifest could not be read or parsed.");
        }

        if (document is null)
        {
            return Quarantined("unknown", "manifest.empty", "Plugin manifest is empty.");
        }

        var pluginDirectory = Path.GetDirectoryName(manifestPath)!;
        if (!string.IsNullOrWhiteSpace(document.Id) &&
            (seenIds.Contains(document.Id) || _loaded.ContainsKey(document.Id)))
        {
            return Quarantined(
                document.Id,
                "manifest.duplicate_id",
                $"Duplicate plugin ID '{document.Id}'.");
        }

        var diagnostics = _validator.Validate(document, pluginDirectory);
        if (diagnostics.Length > 0)
        {
            var state = diagnostics.Any(item =>
                item.Code == "manifest.api_incompatible")
                ? PluginState.Incompatible
                : PluginState.Quarantined;
            var validationResult = new PluginLoadResult(document.Id, state, diagnostics);
            if (!string.IsNullOrWhiteSpace(document.Id))
            {
                seenIds.Add(document.Id);
                _discovered.TryAdd(document.Id, new(manifestPath, document));
                var priorState = _stateRepository is null
                    ? null
                    : await _stateRepository.GetAsync(document.Id, cancellationToken)
                        .ConfigureAwait(false);
                var isEnabled = priorState?.IsEnabled ?? true;
                await PersistStateAsync(
                    document,
                    isEnabled,
                    state,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                UpdateInventory(document.Id, state, diagnostics, isEnabled);
            }

            return validationResult;
        }

        if (!seenIds.Add(document.Id))
        {
            return Quarantined(
                document.Id,
                "manifest.duplicate_id",
                $"Duplicate plugin ID '{document.Id}'.");
        }

        _discovered[document.Id] = new(manifestPath, document);
        _inventory[document.Id] = CreateInventoryItem(
            document,
            PluginState.Discovered,
            [],
            isEnabled: true);
        var persisted = _stateRepository is null
            ? null
            : await _stateRepository.GetAsync(document.Id, cancellationToken)
                .ConfigureAwait(false);
        if (persisted is { IsEnabled: false })
        {
            await PersistStateAsync(
                document,
                isEnabled: false,
                PluginState.Disabled,
                [],
                cancellationToken).ConfigureAwait(false);
            UpdateInventory(document.Id, PluginState.Disabled, [], isEnabled: false);
            return new(document.Id, PluginState.Disabled, []);
        }

        var result = await LoadValidatedManifestAsync(
            manifestPath,
            document,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistStateAsync(
                document,
                isEnabled: true,
                result.State,
                result.Diagnostics,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await UnloadAsync(document.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        UpdateInventory(
            document.Id,
            result.State,
            result.Diagnostics,
            isEnabled: true);
        return result;
    }

    private async Task<PluginLoadResult> LoadValidatedManifestAsync(
        string manifestPath,
        PluginManifestDocument document,
        CancellationToken cancellationToken)
    {
        var pluginDirectory = Path.GetDirectoryName(manifestPath)!;
        var assemblyPath = Path.GetFullPath(Path.Combine(pluginDirectory, document.EntryAssembly));
        if (!File.Exists(assemblyPath))
        {
            return Quarantined(
                document.Id,
                "plugin.assembly_missing",
                "Plugin entry assembly does not exist.");
        }

        var loadContext = new PluginAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var entryType = assembly.GetType(document.EntryType, throwOnError: false);
            if (entryType is null ||
                !typeof(IOrvianPlugin).IsAssignableFrom(entryType) ||
                entryType.IsAbstract)
            {
                loadContext.Unload();
                return Quarantined(
                    document.Id,
                    "plugin.entry_type_invalid",
                    "Plugin entry type is missing or does not implement IOrvianPlugin.");
            }

            if (Activator.CreateInstance(entryType) is not IOrvianPlugin plugin)
            {
                loadContext.Unload();
                return Quarantined(
                    document.Id,
                    "plugin.activation_failed",
                    "Plugin entry type could not be created.");
            }

            var runtimeDiagnostics = ValidateRuntimeIdentity(document, plugin.Manifest);
            if (runtimeDiagnostics.Length > 0)
            {
                DisposeFailedPlugin(plugin, loadContext);
                return new(document.Id, PluginState.Quarantined, runtimeDiagnostics);
            }

            var permissions = document.Permissions.ToImmutableHashSet(StringComparer.Ordinal);
            var builder = new PluginRegistrationBuilder(document.Id, permissions);
            plugin.Configure(builder);
            var registration = _contributionCatalog.Register(builder.Build());
            if (!registration.IsSuccess)
            {
                DisposeFailedPlugin(plugin, loadContext);
                return new(
                    document.Id,
                    PluginState.Quarantined,
                    [
                        .. registration.Errors.Select(error =>
                            new PluginDiagnostic(error.Code, error.Message))
                    ]);
            }

            _featureCatalog.Register(
                document.Id,
                plugin.Manifest.Version,
                permissions,
                builder.LocalHostFeatureProviders,
                builder.FeatureProviders,
                builder.MutationFeatureProviders);

            if (plugin is IAsyncPluginLifecycle lifecycle)
            {
                await lifecycle.ActivateAsync(cancellationToken).ConfigureAwait(false);
            }

            _loaded.Add(document.Id, new(plugin, loadContext));
            return new(document.Id, PluginState.Active, []);
        }
        catch (OperationCanceledException)
        {
            _contributionCatalog.RemovePlugin(document.Id);
            _featureCatalog.RemovePlugin(document.Id);
            loadContext.Unload();
            throw;
        }
        catch (Exception)
        {
            _contributionCatalog.RemovePlugin(document.Id);
            _featureCatalog.RemovePlugin(document.Id);
            loadContext.Unload();
            return new(
                document.Id,
                PluginState.Faulted,
                [new("plugin.unexpected_failure", "Plugin failed during loading or registration.")]);
        }
    }

    private static ImmutableArray<PluginDiagnostic> ValidateRuntimeIdentity(
        PluginManifestDocument document,
        PluginManifest runtime)
    {
        var diagnostics = ImmutableArray.CreateBuilder<PluginDiagnostic>();
        if (!string.Equals(document.Id, runtime.Id, StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                "plugin.identity_mismatch",
                "Runtime plugin ID does not match its manifest."));
        }

        if (!Version.TryParse(document.Version, out var version) || version != runtime.Version)
        {
            diagnostics.Add(new(
                "plugin.version_mismatch",
                "Runtime plugin version does not match its manifest."));
        }

        var declared = document.Permissions.ToImmutableHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(runtime.Permissions))
        {
            diagnostics.Add(new(
                "plugin.permissions_mismatch",
                "Runtime plugin permissions do not match its manifest."));
        }

        return diagnostics.ToImmutable();
    }

    private static PluginLoadResult Quarantined(
        string pluginId,
        string code,
        string message) =>
        new(pluginId, PluginState.Quarantined, [new(code, message)]);

    private Task PersistStateAsync(
        PluginManifestDocument document,
        bool isEnabled,
        PluginState state,
        ImmutableArray<PluginDiagnostic> diagnostics,
        CancellationToken cancellationToken) =>
        _stateRepository?.UpsertAsync(
            new(
                document.Id,
                document.Version,
                isEnabled,
                state.ToString(),
                [.. diagnostics.Select(item =>
                    new PersistedPluginDiagnostic(item.Code, item.Message))],
                DateTimeOffset.UtcNow),
            cancellationToken) ?? Task.CompletedTask;

    private void UpdateInventory(
        string pluginId,
        PluginState state,
        ImmutableArray<PluginDiagnostic> diagnostics,
        bool isEnabled)
    {
        if (_discovered.TryGetValue(pluginId, out var discovered))
        {
            _inventory[pluginId] = CreateInventoryItem(
                discovered.Document,
                state,
                diagnostics,
                isEnabled);
        }
    }

    private static PluginManagementItem CreateInventoryItem(
        PluginManifestDocument document,
        PluginState state,
        ImmutableArray<PluginDiagnostic> diagnostics,
        bool isEnabled) =>
        new(
            document.Id,
            document.Name,
            document.Publisher,
            document.Version,
            state.ToString(),
            isEnabled,
            document.Permissions,
            [.. diagnostics.Select(item =>
                new PersistedPluginDiagnostic(item.Code, item.Message))]);

    private static void DisposeFailedPlugin(
        IOrvianPlugin plugin,
        AssemblyLoadContext loadContext)
    {
        if (plugin is IDisposable disposable)
        {
            disposable.Dispose();
        }

        loadContext.Unload();
    }

    private sealed record LoadedPlugin(
        IOrvianPlugin Instance,
        AssemblyLoadContext LoadContext);

    private sealed record DiscoveredPlugin(
        string ManifestPath,
        PluginManifestDocument Document);

    private sealed record ManifestScan(
        string ManifestPath,
        PluginManifestDocument? Document);

    private sealed class PluginAssemblyLoadContext(string assemblyPath)
        : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Orvian.", StringComparison.Ordinal) == true)
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
