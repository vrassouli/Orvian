using System.Text.Json;
using System.Collections.Immutable;
using Orvian.Application.Contributions;
using Orvian.Application.Plugins;
using Orvian.Plugins.Sample;
using Orvian.Plugins.DateTime;
using Orvian.Plugins.HostOverview;
using Orvian.Plugins.Services;
using Orvian.Plugins.PackageInfo;
using Orvian.Plugins.UsersGroups;
using Orvian.Plugin.Abstractions;
using Orvian.PluginHost;
using Xunit;

namespace Orvian.PluginHost.Tests;

public sealed class PluginHostServiceTests
{
    [Fact]
    public async Task Valid_plugin_loads_and_commits_contributions()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var results = await host.LoadDirectoryAsync(directory.Path);

        var result = Assert.Single(results);
        Assert.Equal(PluginState.Active, result.State);
        Assert.Single(catalog.Snapshot.NavigationPages);
        Assert.Equal("orvian.sample", catalog.Snapshot.NavigationPages[0].PluginId);
        var provider = Assert.Single(host.Features.Snapshot);
        Assert.Equal("sample-system.uname", provider.Provider.ProviderId);
    }

    [Fact]
    public async Task Disabling_plugin_removes_its_contributions()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));
        await host.LoadDirectoryAsync(directory.Path);

        await host.DisableAsync("orvian.sample");

        Assert.Empty(catalog.Snapshot.NavigationPages);
        Assert.Empty(host.Features.Snapshot);
    }

    [Fact]
    public async Task Disabled_plugin_remains_disabled_across_host_restarts()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        var states = new InMemoryPluginStateRepository();

        await using (var firstHost = new PluginHostService(
                         new ContributionCatalog(),
                         new Version(0, 1),
                         stateRepository: states))
        {
            await firstHost.LoadDirectoryAsync(directory.Path);
            await firstHost.DisableAsync("orvian.sample");
        }

        var catalog = new ContributionCatalog();
        await using var secondHost = new PluginHostService(
            catalog,
            new Version(0, 1),
            stateRepository: states);

        var result = Assert.Single(
            await secondHost.LoadDirectoryAsync(directory.Path));

        Assert.Equal(PluginState.Disabled, result.State);
        Assert.Empty(catalog.Snapshot.NavigationPages);
        Assert.Empty(secondHost.Features.Snapshot);
    }

    [Fact]
    public async Task Shutdown_does_not_change_persisted_enabled_preference()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        var states = new InMemoryPluginStateRepository();
        var host = new PluginHostService(
            new ContributionCatalog(),
            new Version(0, 1),
            stateRepository: states);

        await host.LoadDirectoryAsync(directory.Path);
        await host.DisposeAsync();

        var state = await states.GetAsync("orvian.sample");
        Assert.NotNull(state);
        Assert.True(state.IsEnabled);
    }

    [Fact]
    public async Task Disabled_discovered_plugin_can_be_enabled_again()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        var states = new InMemoryPluginStateRepository();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(
            catalog,
            new Version(0, 1),
            stateRepository: states);
        await host.LoadDirectoryAsync(directory.Path);
        await host.DisableAsync("orvian.sample");

        var result = await host.EnableAsync("orvian.sample");

        Assert.Equal(PluginState.Active, result.State);
        Assert.Single(catalog.Snapshot.NavigationPages);
        Assert.True((await states.GetAsync("orvian.sample"))!.IsEnabled);
    }

    [Fact]
    public async Task Management_inventory_exposes_metadata_permissions_and_state()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        await using var host = new PluginHostService(
            new ContributionCatalog(),
            new Version(0, 1),
            stateRepository: new InMemoryPluginStateRepository());
        await host.LoadDirectoryAsync(directory.Path);

        var item = Assert.Single(await host.GetPluginsAsync());

        Assert.Equal("Sample Plugin", item.Name);
        Assert.Equal("Orvian", item.Publisher);
        Assert.Equal("0.1.0", item.Version);
        Assert.Equal("Active", item.LifecycleState);
        Assert.True(item.IsEnabled);
        Assert.Contains("command.read.execute", item.Permissions);
    }

    [Fact]
    public async Task Persistence_failure_rolls_back_loaded_contributions()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(
            catalog,
            new Version(0, 1),
            stateRepository: new FailingPluginStateRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.LoadDirectoryAsync(directory.Path));

        Assert.Empty(catalog.Snapshot.NavigationPages);
        Assert.Empty(host.Features.Snapshot);
    }

    [Fact]
    public async Task Duplicate_plugin_id_is_quarantined()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin("first");
        directory.AddSamplePlugin("second");
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var results = await host.LoadDirectoryAsync(directory.Path);

        Assert.Equal(2, results.Count);
        Assert.Single(results, result => result.State == PluginState.Active);
        var quarantined = Assert.Single(
            results,
            result => result.State == PluginState.Quarantined);
        Assert.Contains(
            quarantined.Diagnostics,
            diagnostic => diagnostic.Code == "manifest.duplicate_id");
        Assert.Single(catalog.Snapshot.NavigationPages);
    }

    [Fact]
    public async Task Malformed_manifest_does_not_stop_other_plugins()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddMalformedManifest("broken");
        directory.AddSamplePlugin("valid");
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var results = await host.LoadDirectoryAsync(directory.Path);

        Assert.Contains(results, result => result.State == PluginState.Quarantined);
        Assert.Contains(results, result => result.State == PluginState.Active);
        Assert.Single(catalog.Snapshot.NavigationPages);
    }

    [Fact]
    public async Task DateTimePluginLoadsProvidersThroughPublicSdk()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddDateTimePlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var result = Assert.Single(await host.LoadDirectoryAsync(directory.Path));

        Assert.Equal(PluginState.Active, result.State);
        Assert.Equal(2, host.Features.Snapshot.Length);
        Assert.All(
            host.Features.Snapshot,
            provider => Assert.Equal("orvian.datetime", provider.PluginId));
        var page = Assert.Single(catalog.Snapshot.NavigationPages);
        Assert.Equal("Date & Time", page.Title);
        Assert.Equal("date-time", page.FeatureId);
        var mutations = host.Features.MutationSnapshot;
        Assert.Equal(2, mutations.Length);
        Assert.Equal(
            ["datetime.change", "timezone.change"],
            mutations.Select(item => item.Provider.MutationId)
                .Order(StringComparer.Ordinal));
        Assert.All(
            mutations,
            mutation => Assert.Contains(
                PluginPermissions.CommandMutateExecute,
                mutation.Permissions));
    }

    [Fact]
    public async Task ServicesPluginLoadsReadAndMutationProvidersThroughPublicSdk()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddServicesPlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var result = Assert.Single(await host.LoadDirectoryAsync(directory.Path));

        Assert.Equal(PluginState.Active, result.State);
        Assert.Single(host.Features.Snapshot);
        Assert.Equal(5, host.Features.MutationSnapshot.Length);
        var page = Assert.Single(catalog.Snapshot.NavigationPages);
        Assert.Equal("services", page.FeatureId);
    }

    [Fact]
    public async Task HostOverviewPluginLoadsLocalProviderWithoutCommandPermission()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddHostOverviewPlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var result = Assert.Single(await host.LoadDirectoryAsync(directory.Path));

        Assert.Equal(PluginState.Active, result.State);
        Assert.Empty(host.Features.Snapshot);
        var provider = Assert.Single(host.Features.LocalHostSnapshot);
        Assert.Equal("host-overview.core-context", provider.Provider.ProviderId);
        Assert.Contains(PluginPermissions.HostRead, provider.Permissions);
        Assert.DoesNotContain(
            PluginPermissions.CommandReadExecute,
            provider.Permissions);
        var page = Assert.Single(catalog.Snapshot.NavigationPages);
        Assert.Equal("host-overview", page.FeatureId);
    }

    [Fact]
    public async Task AllOperationalPluginsLoadTogetherWithoutConcreteCoreReferences()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddDateTimePlugin();
        directory.AddHostOverviewPlugin();
        directory.AddServicesPlugin();
        directory.AddPackageInfoPlugin();
        directory.AddUsersGroupsPlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var results = await host.LoadDirectoryAsync(directory.Path);

        Assert.Equal(5, results.Count);
        Assert.All(results, result => Assert.Equal(PluginState.Active, result.State));
        Assert.Equal(5, catalog.Snapshot.NavigationPages.Length);
        Assert.Single(host.Features.LocalHostSnapshot);
        Assert.Equal(12, host.Features.Snapshot.Length);
        Assert.Equal(7, host.Features.MutationSnapshot.Length);
    }

    [Fact]
    public async Task Dependency_is_loaded_before_dependent_regardless_of_path_order()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddDateTimePlugin("a-date-time", ["orvian.services"]);
        directory.AddServicesPlugin("z-services");
        await using var host = new PluginHostService(
            new ContributionCatalog(),
            new Version(0, 1));

        var results = await host.LoadDirectoryAsync(directory.Path);

        Assert.Equal(
            ["orvian.services", "orvian.datetime"],
            results.Select(result => result.PluginId));
        Assert.All(results, result => Assert.Equal(PluginState.Active, result.State));
    }

    [Fact]
    public async Task Missing_dependency_makes_plugin_incompatible_without_loading_it()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin(dependencies: ["orvian.missing"]);
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var result = Assert.Single(await host.LoadDirectoryAsync(directory.Path));

        Assert.Equal(PluginState.Incompatible, result.State);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "plugin.dependency_missing");
        Assert.Empty(catalog.Snapshot.NavigationPages);
    }

    [Fact]
    public async Task Failed_dependency_prevents_dependent_from_loading()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        directory.RemoveAssembly("sample", "Orvian.Plugins.Sample.dll");
        directory.AddDateTimePlugin("date-time", ["orvian.sample"]);
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var results = await host.LoadDirectoryAsync(directory.Path);

        Assert.Equal(
            PluginState.Quarantined,
            Assert.Single(results, item => item.PluginId == "orvian.sample").State);
        var dependent = Assert.Single(
            results,
            item => item.PluginId == "orvian.datetime");
        Assert.Equal(PluginState.Incompatible, dependent.State);
        Assert.Contains(
            dependent.Diagnostics,
            diagnostic => diagnostic.Code == "plugin.dependency_unavailable");
        Assert.Empty(catalog.Snapshot.NavigationPages);
    }

    [Fact]
    public async Task Circular_dependencies_are_incompatible_and_contribute_nothing()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddDateTimePlugin("date-time", ["orvian.services"]);
        directory.AddServicesPlugin("services", ["orvian.datetime"]);
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var results = await host.LoadDirectoryAsync(directory.Path);

        Assert.Equal(2, results.Count);
        Assert.All(
            results,
            result =>
            {
                Assert.Equal(PluginState.Incompatible, result.State);
                Assert.Contains(
                    result.Diagnostics,
                    diagnostic => diagnostic.Code == "plugin.dependency_cycle");
            });
        Assert.Empty(catalog.Snapshot.NavigationPages);
        Assert.Empty(host.Features.Snapshot);
        Assert.Empty(host.Features.MutationSnapshot);
    }

    [Fact]
    public async Task Active_dependency_cannot_be_disabled_before_its_dependent()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddDateTimePlugin("date-time", ["orvian.services"]);
        directory.AddServicesPlugin();
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));
        await host.LoadDirectoryAsync(directory.Path);

        var exception = await Assert.ThrowsAsync<PluginStateChangeException>(
            () => host.DisableAsync("orvian.services"));

        Assert.Contains("Date & Time", exception.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(2, catalog.Snapshot.NavigationPages.Length);
    }

    [Fact]
    public async Task Dependent_cannot_be_enabled_until_dependency_is_active()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddDateTimePlugin("date-time", ["orvian.services"]);
        directory.AddServicesPlugin();
        await using var host = new PluginHostService(
            new ContributionCatalog(),
            new Version(0, 1),
            stateRepository: new InMemoryPluginStateRepository());
        await host.LoadDirectoryAsync(directory.Path);
        await host.DisableAsync("orvian.datetime");
        await host.DisableAsync("orvian.services");

        var exception = await Assert.ThrowsAsync<PluginStateChangeException>(
            () => host.EnableAsync("orvian.datetime"));

        Assert.Contains("orvian.services", exception.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(
            PluginState.Active,
            (await host.EnableAsync("orvian.services")).State);
        Assert.Equal(
            PluginState.Active,
            (await host.EnableAsync("orvian.datetime")).State);
    }

    [Fact]
    public async Task Api_incompatible_plugin_is_not_loaded_and_appears_in_inventory()
    {
        using var directory = new TemporaryPluginDirectory();
        directory.AddSamplePlugin();
        directory.SetApiVersion("sample", "1.0.0");
        var catalog = new ContributionCatalog();
        await using var host = new PluginHostService(catalog, new Version(0, 1));

        var result = Assert.Single(await host.LoadDirectoryAsync(directory.Path));
        var inventory = Assert.Single(await host.GetPluginsAsync());

        Assert.Equal(PluginState.Incompatible, result.State);
        Assert.Equal("Incompatible", inventory.LifecycleState);
        Assert.Contains(
            inventory.Diagnostics,
            diagnostic => diagnostic.Code == "manifest.api_incompatible");
        Assert.Empty(catalog.Snapshot.NavigationPages);
    }

    private sealed class TemporaryPluginDirectory : IDisposable
    {
        public TemporaryPluginDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"orvian-plugin-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void AddSamplePlugin(
            string name = "sample",
            ImmutableArray<string> dependencies = default)
        {
            var pluginDirectory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(pluginDirectory);
            var sourceAssembly = typeof(SamplePlugin).Assembly.Location;
            File.Copy(
                sourceAssembly,
                System.IO.Path.Combine(pluginDirectory, "Orvian.Plugins.Sample.dll"));

            var document = new PluginManifestDocument
            {
                Id = "orvian.sample",
                Name = "Sample Plugin",
                Description = "Test sample.",
                Publisher = "Orvian",
                Version = "0.1.0",
                OrvianApiVersion = "0.1.0",
                EntryAssembly = "Orvian.Plugins.Sample.dll",
                EntryType = typeof(SamplePlugin).FullName!,
                Permissions =
                [
                    "ui.navigation.contribute",
                    "command.read.execute"
                ],
                Dependencies = dependencies.IsDefault ? [] : dependencies
            };
            File.WriteAllText(
                System.IO.Path.Combine(pluginDirectory, "orvian.plugin.json"),
                JsonSerializer.Serialize(document));
        }

        public void AddMalformedManifest(string name)
        {
            var pluginDirectory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(pluginDirectory);
            File.WriteAllText(
                System.IO.Path.Combine(pluginDirectory, "orvian.plugin.json"),
                "{not-json");
        }

        public void AddDateTimePlugin(
            string folder = "date-time",
            ImmutableArray<string> dependencies = default)
            => AddPlugin(
                folder,
                typeof(DateTimePlugin),
                "Orvian.Plugins.DateTime.dll",
                dependencies);

        public void AddHostOverviewPlugin()
            => AddPlugin(
                "host-overview",
                typeof(HostOverviewPlugin),
                "Orvian.Plugins.HostOverview.dll");

        public void AddServicesPlugin(
            string folder = "services",
            ImmutableArray<string> dependencies = default)
            => AddPlugin(
                folder,
                typeof(ServicesPlugin),
                "Orvian.Plugins.Services.dll",
                dependencies);

        public void AddPackageInfoPlugin() =>
            AddPlugin(
                "package-info",
                typeof(PackageInfoPlugin),
                "Orvian.Plugins.PackageInfo.dll");

        public void AddUsersGroupsPlugin() =>
            AddPlugin(
                "users-groups",
                typeof(UsersGroupsPlugin),
                "Orvian.Plugins.UsersGroups.dll");

        public void RemoveAssembly(string folder, string assemblyName) =>
            File.Delete(System.IO.Path.Combine(Path, folder, assemblyName));

        public void SetApiVersion(string folder, string apiVersion)
        {
            var path = System.IO.Path.Combine(
                Path,
                folder,
                "orvian.plugin.json");
            var document = JsonSerializer.Deserialize<PluginManifestDocument>(
                File.ReadAllText(path));
            Assert.NotNull(document);
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(document with
                {
                    OrvianApiVersion = apiVersion
                }));
        }

        private void AddPlugin(
            string folder,
            Type pluginType,
            string assemblyName,
            ImmutableArray<string> dependencies = default)
        {
            var pluginDirectory = System.IO.Path.Combine(Path, folder);
            Directory.CreateDirectory(pluginDirectory);
            var assemblyPath = pluginType.Assembly.Location;
            File.Copy(
                assemblyPath,
                System.IO.Path.Combine(pluginDirectory, assemblyName));
            var plugin = Assert.IsAssignableFrom<IOrvianPlugin>(
                Activator.CreateInstance(pluginType));
            var manifest = plugin.Manifest;
            var document = new PluginManifestDocument
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Description = manifest.Description,
                Publisher = manifest.Publisher,
                Version = manifest.Version.ToString(),
                OrvianApiVersion = manifest.OrvianApiVersion.ToString(),
                EntryAssembly = assemblyName,
                EntryType = pluginType.FullName!,
                Permissions = [.. manifest.Permissions],
                Dependencies = dependencies.IsDefault ? [] : dependencies
            };
            File.WriteAllText(
                System.IO.Path.Combine(pluginDirectory, "orvian.plugin.json"),
                JsonSerializer.Serialize(document));
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class InMemoryPluginStateRepository : IPluginStateRepository
    {
        private readonly Dictionary<string, PersistedPluginState> _states =
            new(StringComparer.Ordinal);

        public Task<PersistedPluginState?> GetAsync(
            string pluginId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _states.TryGetValue(pluginId, out var state);
            return Task.FromResult(state);
        }

        public Task UpsertAsync(
            PersistedPluginState state,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _states[state.PluginId] = state with
            {
                Diagnostics = state.Diagnostics.IsDefault
                    ? ImmutableArray<PersistedPluginDiagnostic>.Empty
                    : state.Diagnostics
            };
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPluginStateRepository : IPluginStateRepository
    {
        public Task<PersistedPluginState?> GetAsync(
            string pluginId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PersistedPluginState?>(null);

        public Task UpsertAsync(
            PersistedPluginState state,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Persistence unavailable."));
    }
}
