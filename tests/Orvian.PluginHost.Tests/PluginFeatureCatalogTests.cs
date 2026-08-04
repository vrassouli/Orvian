using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;
using Orvian.PluginHost;
using Xunit;

namespace Orvian.PluginHost.Tests;

public sealed class PluginFeatureCatalogTests
{
    [Fact]
    public void RegisterAndRemoveArePluginScoped()
    {
        var catalog = new PluginFeatureCatalog();
        catalog.Register(
            "plugin.one",
            new Version(1, 0),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                PluginPermissions.CommandReadExecute),
            [new Provider("provider.one")]);
        catalog.Register(
            "plugin.two",
            new Version(1, 0),
            ImmutableHashSet<string>.Empty,
            [new Provider("provider.two")]);

        catalog.RemovePlugin("plugin.one");

        var remaining = Assert.Single(catalog.Snapshot);
        Assert.Equal("plugin.two", remaining.PluginId);
        Assert.Equal("provider.two", remaining.Provider.ProviderId);
    }

    [Fact]
    public void DuplicateProviderIdIsRejectedWithoutPartialRegistration()
    {
        var catalog = new PluginFeatureCatalog();
        catalog.Register(
            "plugin.one",
            new Version(1, 0),
            ImmutableHashSet<string>.Empty,
            [new Provider("provider.shared")]);

        Assert.Throws<InvalidOperationException>(() => catalog.Register(
            "plugin.two",
            new Version(1, 0),
            ImmutableHashSet<string>.Empty,
            [new Provider("provider.shared")]));
        Assert.Single(catalog.Snapshot);
    }

    private sealed class Provider(string providerId) : IReadOnlyFeatureProvider
    {
        public string FeatureId => "feature";

        public string ProviderId => providerId;

        public int Priority => 0;

        public IReadOnlySet<string> RequiredCapabilities =>
            ImmutableHashSet<string>.Empty;

        public PluginReadRequest CreateRequest() =>
            new("true", [], TimeSpan.FromSeconds(1));

        public PluginFeatureParseResult Parse(PluginCommandOutput output) =>
            new(new(ImmutableDictionary<string, string>.Empty));
    }
}
