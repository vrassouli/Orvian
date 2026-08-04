using System.Collections.Immutable;
using Orvian.Application.Contributions;
using Orvian.UI.Abstractions;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class ContributionCatalogTests
{
    [Fact]
    public void Valid_batch_is_committed_as_one_snapshot()
    {
        var catalog = new ContributionCatalog();
        var batch = CreateBatch("orvian.sample", "/plugins/orvian.sample/overview");

        var result = catalog.Register(batch);

        Assert.True(result.IsSuccess);
        Assert.Single(catalog.Snapshot.NavigationPages);
        Assert.Single(catalog.Snapshot.Commands);
        Assert.Single(catalog.Snapshot.Settings);
    }

    [Fact]
    public void Duplicate_in_any_registry_rolls_back_entire_batch()
    {
        var catalog = new ContributionCatalog();
        Assert.True(catalog.Register(CreateBatch("orvian.first", "/plugins/first")).IsSuccess);

        var duplicateCommand = CreateBatch("orvian.second", "/plugins/second") with
        {
            Commands =
            [
                CreateCommand("orvian.second", "refresh")
            ]
        };

        var result = catalog.Register(duplicateCommand);

        Assert.False(result.IsSuccess);
        Assert.Single(catalog.Snapshot.NavigationPages);
        Assert.DoesNotContain(
            catalog.Snapshot.NavigationPages,
            item => item.PluginId == "orvian.second");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/activity")]
    [InlineData("/SETTINGS")]
    public void Core_route_cannot_be_shadowed(string route)
    {
        var catalog = new ContributionCatalog();

        var result = catalog.Register(CreateBatch("orvian.sample", route));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == "navigation.core_route");
        Assert.Empty(catalog.Snapshot.NavigationPages);
    }

    [Fact]
    public void Contribution_cannot_claim_another_plugin_identity()
    {
        var catalog = new ContributionCatalog();
        var batch = CreateBatch("orvian.sample", "/plugins/sample") with
        {
            NavigationPages =
            [
                CreatePage("orvian.impostor", "/plugins/sample")
            ]
        };

        var result = catalog.Register(batch);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors, error => error.Code == "navigation.plugin_mismatch");
        Assert.Empty(catalog.Snapshot.NavigationPages);
    }

    [Fact]
    public void Removing_plugin_removes_only_its_contributions()
    {
        var catalog = new ContributionCatalog();
        Assert.True(catalog.Register(CreateBatch("orvian.first", "/plugins/first")).IsSuccess);
        Assert.True(catalog.Register(CreateBatch("orvian.second", "/plugins/second", "second")).IsSuccess);

        catalog.RemovePlugin("orvian.first");

        Assert.All(
            catalog.Snapshot.NavigationPages,
            item => Assert.Equal("orvian.second", item.PluginId));
        Assert.All(
            catalog.Snapshot.Commands,
            item => Assert.Equal("orvian.second", item.PluginId));
        Assert.All(
            catalog.Snapshot.Settings,
            item => Assert.Equal("orvian.second", item.PluginId));
    }

    private static ContributionBatch CreateBatch(
        string pluginId,
        string route,
        string suffix = "") =>
        new(
            pluginId,
            [CreatePage(pluginId, route, suffix)],
            [CreateCommand(pluginId, $"refresh{suffix}")],
            [
                new(
                    pluginId,
                    $"settings{suffix}",
                    "Sample settings",
                    "Configure the sample plugin.",
                    "Orvian.Sample.SettingsViewModel")
            ]);

    private static NavigationPageContribution CreatePage(
        string pluginId,
        string route,
        string suffix = "") =>
        new(
            pluginId,
            $"overview{suffix}",
            route,
            "Overview",
            "Open plugin overview",
            "puzzle",
            "Orvian.Sample.OverviewViewModel",
            ImmutableArray<string>.Empty);

    private static CommandContribution CreateCommand(string pluginId, string id) =>
        new(
            pluginId,
            id,
            "Refresh",
            "Refresh plugin data.",
            "Orvian.Sample.RefreshHandler",
            ImmutableArray<string>.Empty,
            ["command.read.execute"]);
}
