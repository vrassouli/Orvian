using Microsoft.Data.Sqlite;
using Orvian.Application.Settings;
using Orvian.Persistence;
using Xunit;

namespace Orvian.Persistence.Tests;

public sealed class SqliteApplicationSettingsRepositoryTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"orvian-settings-{Guid.NewGuid():N}.db");
    private OrvianDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = new OrvianDatabase(_databasePath);
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task MissingSettingsLoadConservativeDefaults()
    {
        var result = await new SqliteApplicationSettingsRepository(_database).LoadAsync();

        Assert.Equal(ApplicationSettings.Default, result.Settings);
        Assert.False(result.UsedDefaults);
        Assert.True(result.Settings.ClearSessionCredentialsOnDisconnect);
    }

    [Fact]
    public async Task SettingsRoundTripAtomicallyAndPreserveUnknownKeys()
    {
        var repository = new SqliteApplicationSettingsRepository(_database);
        await InsertRawAsync("future.setting", 2, """{"future":true}""");
        var settings = ApplicationSettings.Default with
        {
            Appearance = AppearancePreference.Dark,
            CultureName = "en-GB",
            DefaultConnectionTimeout = TimeSpan.FromSeconds(42),
            DefaultMaximumReconnectAttempts = 4,
            OutputRetentionDays = 14,
            AuditRetentionDays = 120,
            ClearSessionCredentialsOnDisconnect = false
        };

        await repository.SaveAsync(settings);

        Assert.Equal(settings, (await repository.LoadAsync()).Settings);
        Assert.Equal(
            """{"future":true}""",
            await ReadRawValueAsync("future.setting"));
    }

    [Fact]
    public async Task InvalidSaveLeavesExistingSettingsUnchanged()
    {
        var repository = new SqliteApplicationSettingsRepository(_database);
        await repository.SaveAsync(ApplicationSettings.Default);
        var invalid = ApplicationSettings.Default with
        {
            OutputRetentionDays = 91,
            AuditRetentionDays = 90
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.SaveAsync(invalid));

        Assert.Equal(ApplicationSettings.Default, (await repository.LoadAsync()).Settings);
    }

    [Fact]
    public async Task CorruptKnownSettingUsesDefaultsWithVisibleDiagnostic()
    {
        await InsertRawAsync("retention.audit_days", 1, "\"not-a-number\"");

        var result = await new SqliteApplicationSettingsRepository(_database).LoadAsync();

        Assert.True(result.UsedDefaults);
        Assert.Equal(ApplicationSettings.Default, result.Settings);
        Assert.NotNull(result.SafeDiagnostic);
        Assert.Equal("\"not-a-number\"", await ReadRawValueAsync("retention.audit_days"));
    }

    [Fact]
    public async Task NewerKnownSettingIsPreservedAndFailsSafeToDefaults()
    {
        await InsertRawAsync("appearance", 99, "2");

        var result = await new SqliteApplicationSettingsRepository(_database).LoadAsync();

        Assert.True(result.UsedDefaults);
        Assert.Equal(ApplicationSettings.Default, result.Settings);
        Assert.Equal("2", await ReadRawValueAsync("appearance"));
    }

    [Fact]
    public async Task ConcurrentSavesNeverExposeMixedSettings()
    {
        var first = ApplicationSettings.Default with
        {
            Appearance = AppearancePreference.Light,
            OutputRetentionDays = 10,
            AuditRetentionDays = 100
        };
        var second = ApplicationSettings.Default with
        {
            Appearance = AppearancePreference.Dark,
            OutputRetentionDays = 20,
            AuditRetentionDays = 200
        };
        var firstRepository = new SqliteApplicationSettingsRepository(_database);
        var secondRepository = new SqliteApplicationSettingsRepository(
            new OrvianDatabase(_databasePath));

        await Task.WhenAll(
            firstRepository.SaveAsync(first),
            secondRepository.SaveAsync(second));
        var loaded = (await firstRepository.LoadAsync()).Settings;

        Assert.True(loaded == first || loaded == second);
    }

    private async Task InsertRawAsync(string key, int version, string json)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO application_settings (
                setting_key, schema_version, value_json, updated_at_utc)
            VALUES ($key, $version, $json, '2026-01-01T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadRawValueAsync(string key)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value_json FROM application_settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync();
    }
}
