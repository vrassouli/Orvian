using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;
using Orvian.UI.Abstractions;

namespace Orvian.PluginHost;

internal sealed class PluginRegistrationBuilder(
    string pluginId,
    IReadOnlySet<string> permissions) : IPluginBuilder
{
    private readonly List<NavigationPageContribution> _navigation = [];
    private readonly List<CommandContribution> _commands = [];
    private readonly List<SettingsContribution> _settings = [];
    private readonly HashSet<string> _requiredCapabilities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _providedCapabilities = new(StringComparer.Ordinal);
    private readonly List<IReadOnlyFeatureProvider> _features = [];

    public INavigationRegistry Navigation { get; } =
        new NavigationRegistration(pluginId, permissions);

    public ICommandRegistry Commands { get; } =
        new CommandRegistration(pluginId, permissions);

    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistration();

    public ISettingsRegistry Settings { get; } =
        new SettingsRegistration(pluginId, permissions);

    public IFeatureProviderRegistry Features { get; } =
        new FeatureProviderRegistration(pluginId, permissions);

    public ImmutableArray<IReadOnlyFeatureProvider> FeatureProviders =>
        [.. ((FeatureProviderRegistration)Features).ReadItems];

    public ImmutableArray<ILocalHostFeatureProvider> LocalHostFeatureProviders =>
        [.. ((FeatureProviderRegistration)Features).LocalHostItems];

    public ImmutableArray<IMutationFeatureProvider> MutationFeatureProviders =>
        [.. ((FeatureProviderRegistration)Features).MutationItems];

    public ContributionBatch Build()
    {
        _navigation.AddRange(((NavigationRegistration)Navigation).Items);
        _commands.AddRange(((CommandRegistration)Commands).Items);
        _settings.AddRange(((SettingsRegistration)Settings).Items);
        _requiredCapabilities.UnionWith(((CapabilityRegistration)Capabilities).Required);
        _providedCapabilities.UnionWith(((CapabilityRegistration)Capabilities).Provided);

        return new ContributionBatch(
            pluginId,
            [.. _navigation],
            [.. _commands],
            [.. _settings]);
    }

    private sealed class FeatureProviderRegistration(
        string pluginId,
        IReadOnlySet<string> permissions) : IFeatureProviderRegistry
    {
        public List<IReadOnlyFeatureProvider> ReadItems { get; } = [];

        public List<ILocalHostFeatureProvider> LocalHostItems { get; } = [];

        public List<IMutationFeatureProvider> MutationItems { get; } = [];

        public void Add(ILocalHostFeatureProvider provider)
        {
            Demand(permissions, PluginPermissions.HostRead);
            ArgumentNullException.ThrowIfNull(provider);
            ValidateProvider(
                provider.FeatureId,
                provider.ProviderId,
                provider.RequiredCapabilities);
            if (LocalHostItems.Any(item =>
                    string.Equals(
                        item.ProviderId,
                        provider.ProviderId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' registered duplicate local host provider ID " +
                    $"'{provider.ProviderId}'.");
            }

            LocalHostItems.Add(provider);
        }

        public void Add(IReadOnlyFeatureProvider provider)
        {
            Demand(permissions, PluginPermissions.CommandReadExecute);
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.FeatureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderId);
            foreach (var capability in provider.RequiredCapabilities)
            {
                ValidateCapability(capability);
            }

            if (ReadItems.Any(item =>
                    string.Equals(
                        item.ProviderId,
                        provider.ProviderId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' registered duplicate provider ID " +
                    $"'{provider.ProviderId}'.");
            }

            ReadItems.Add(provider);
        }

        public void Add(IMutationFeatureProvider provider)
        {
            Demand(permissions, PluginPermissions.CommandMutateExecute);
            Demand(permissions, PluginPermissions.PrivilegeElevatedRequest);
            ArgumentNullException.ThrowIfNull(provider);
            ValidateProvider(
                provider.FeatureId,
                provider.ProviderId,
                provider.RequiredCapabilities);
            if (MutationItems.Any(item =>
                    string.Equals(
                        item.ProviderId,
                        provider.ProviderId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' registered duplicate mutation provider ID " +
                    $"'{provider.ProviderId}'.");
            }

            MutationItems.Add(provider);
        }

        private static void ValidateProvider(
            string featureId,
            string providerId,
            IEnumerable<string> capabilities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
            foreach (var capability in capabilities)
            {
                ValidateCapability(capability);
            }
        }
    }

    private sealed class NavigationRegistration(
        string pluginId,
        IReadOnlySet<string> permissions) : INavigationRegistry
    {
        public List<NavigationPageContribution> Items { get; } = [];

        public void AddPage(string route, string title, string icon, Type viewModelType)
        {
            AddPageCore(route, title, icon, viewModelType, null);
        }

        public void AddFeaturePage(
            string route,
            string title,
            string icon,
            Type viewModelType,
            string featureId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
            AddPageCore(route, title, icon, viewModelType, featureId);
        }

        private void AddPageCore(
            string route,
            string title,
            string icon,
            Type viewModelType,
            string? featureId)
        {
            Demand(permissions, PluginPermissions.UiNavigationContribute);
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(viewModelType);

            var id = route.Trim('/').Replace('/', '.');
            Items.Add(new(
                pluginId,
                id,
                route,
                title,
                title,
                icon,
                viewModelType.AssemblyQualifiedName ?? viewModelType.FullName ?? viewModelType.Name,
                [],
                FeatureId: featureId));
        }
    }

    private sealed class CommandRegistration(
        string pluginId,
        IReadOnlySet<string> permissions) : ICommandRegistry
    {
        public List<CommandContribution> Items { get; } = [];

        public void Add(string id, string title, Type handlerType)
        {
            Demand(permissions, PluginPermissions.UiCommandContribute);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(handlerType);

            Items.Add(new(
                pluginId,
                id,
                title,
                title,
                handlerType.AssemblyQualifiedName ?? handlerType.FullName ?? handlerType.Name,
                [],
                [PluginPermissions.UiCommandContribute]));
        }
    }

    private sealed class CapabilityRegistration : ICapabilityRegistry
    {
        public HashSet<string> Required { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Provided { get; } = new(StringComparer.Ordinal);

        public void Require(string capability)
        {
            ValidateCapability(capability);
            Required.Add(capability);
        }

        public void Provide(string capability)
        {
            ValidateCapability(capability);
            Provided.Add(capability);
        }
    }

    private sealed class SettingsRegistration(
        string pluginId,
        IReadOnlySet<string> permissions) : ISettingsRegistry
    {
        public List<SettingsContribution> Items { get; } = [];

        public void AddPage(
            string id,
            string title,
            string description,
            Type viewModelType,
            int sortOrder = 0)
        {
            Demand(permissions, PluginPermissions.SettingsPluginReadWrite);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(viewModelType);

            Items.Add(new(
                pluginId,
                id,
                title,
                description,
                viewModelType.AssemblyQualifiedName ?? viewModelType.FullName ?? viewModelType.Name,
                sortOrder));
        }
    }

    private static void Demand(IReadOnlySet<string> permissions, string permission)
    {
        if (!permissions.Contains(permission))
        {
            throw new InvalidOperationException(
                $"Plugin did not declare required permission '{permission}'.");
        }
    }

    private static void ValidateCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        if (capability.Any(character =>
                !(char.IsLower(character) || char.IsDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException(
                "Capabilities must use lowercase dotted identifiers.",
                nameof(capability));
        }
    }
}
