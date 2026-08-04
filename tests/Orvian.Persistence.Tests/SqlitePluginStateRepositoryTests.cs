using Orvian.Application.Plugins;
using Xunit;

namespace Orvian.Persistence.Tests;

public sealed class SqlitePluginStateRepositoryTests
{
    [Fact]
    public async Task State_round_trips_and_can_be_updated()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"orvian-plugin-state-{Guid.NewGuid():N}.db");
        try
        {
            var database = new OrvianDatabase(path);
            await database.InitializeAsync();
            var repository = new SqlitePluginStateRepository(database);
            var observedAt = DateTimeOffset.UtcNow;
            await repository.UpsertAsync(new(
                "orvian.sample",
                "0.1.0",
                true,
                "Active",
                [new("plugin.warning", "Safe warning.")],
                observedAt));

            var active = await repository.GetAsync("orvian.sample");

            Assert.NotNull(active);
            Assert.True(active.IsEnabled);
            Assert.Equal("Active", active.LifecycleState);
            Assert.Equal("plugin.warning", Assert.Single(active.Diagnostics).Code);

            await repository.UpsertAsync(active with
            {
                IsEnabled = false,
                LifecycleState = "Disabled",
                Diagnostics = [],
                UpdatedAt = observedAt.AddMinutes(1)
            });

            var disabled = await repository.GetAsync("orvian.sample");
            Assert.NotNull(disabled);
            Assert.False(disabled.IsEnabled);
            Assert.Equal("Disabled", disabled.LifecycleState);
            Assert.Empty(disabled.Diagnostics);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Missing_plugin_returns_null()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"orvian-plugin-state-{Guid.NewGuid():N}.db");
        try
        {
            var database = new OrvianDatabase(path);
            await database.InitializeAsync();
            var repository = new SqlitePluginStateRepository(database);

            Assert.Null(await repository.GetAsync("orvian.missing"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
