using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;
using Orvian.Plugins.HostOverview;
using Xunit;

namespace Orvian.Plugins.HostOverview.Tests;

public sealed class HostOverviewProviderTests
{
    [Fact]
    public void ConnectedContextBuildsTrustedCurrentOverview()
    {
        var provider = new HostOverviewProvider();
        var context = CreateContext(
            "Ready",
            DateTimeOffset.Parse("2026-07-29T10:00:00Z"),
            isPartial: false);

        var result = provider.Build(context);

        Assert.True(result.IsSuccess);
        var values = Assert.IsType<PluginFeatureReadModel>(result.Model).Values;
        Assert.Equal("Ubuntu 24.04 LTS", values["operating_system"]);
        Assert.Equal("2d 2h 0m", values["uptime"]);
        Assert.Contains("Current · complete", values["discovery"]);
        Assert.Equal("sudo", values["privilege_provider"]);
        Assert.Contains("Packages: apt", values["active_providers"]);
        Assert.Contains("SHA256:trusted", values["trusted_identity"]);
    }

    [Fact]
    public void DisconnectedPartialContextMarksCachedDataStale()
    {
        var provider = new HostOverviewProvider();
        var context = CreateContext(
            "Disconnected",
            DateTimeOffset.Parse("2026-07-29T09:00:00Z"),
            isPartial: true);

        var result = provider.Build(context);

        var values = Assert.IsType<PluginFeatureReadModel>(result.Model).Values;
        Assert.Contains("Cached · stale · partial", values["discovery"]);
    }

    [Fact]
    public void MissingDiscoveryAndTrustProduceExplicitUnavailableStates()
    {
        var provider = new HostOverviewProvider();
        var context = new LocalHostFeatureContext(
            "New host",
            "new.example:22",
            "Disconnected",
            null,
            null,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            null,
            false,
            "Unknown",
            ImmutableDictionary<string, string>.Empty,
            ImmutableHashSet<string>.Empty);

        var result = provider.Build(context);

        var values = Assert.IsType<PluginFeatureReadModel>(result.Model).Values;
        Assert.Equal("Not trusted yet", values["trusted_identity"]);
        Assert.Equal("Not available", values["discovery"]);
        Assert.Equal("Unavailable", values["uptime"]);
        Assert.Equal("None discovered", values["capabilities"]);
    }

    private static LocalHostFeatureContext CreateContext(
        string connectionState,
        DateTimeOffset discoveryCompletedAt,
        bool isPartial) =>
        new(
            "Production",
            "prod.example:22",
            connectionState,
            "ssh-ed25519",
            "SHA256:trusted",
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            discoveryCompletedAt,
            isPartial,
            "Linux",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["os.pretty_name"] = "Ubuntu 24.04 LTS",
                ["host.name"] = "prod-01",
                ["kernel.release"] = "6.8.0",
                ["kernel.arch"] = "aarch64",
                ["system.boot_time"] = "2026-07-27T10:00:00Z",
                ["init.system"] = "systemd",
                ["package.provider"] = "apt"
            }.ToImmutableDictionary(StringComparer.Ordinal),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                "privilege.sudo",
                "time.systemd"));
}
