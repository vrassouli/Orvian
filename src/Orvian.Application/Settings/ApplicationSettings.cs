using System.Globalization;

namespace Orvian.Application.Settings;

public enum AppearancePreference
{
    System,
    Light,
    Dark
}

public sealed record ApplicationSettings(
    AppearancePreference Appearance,
    string? CultureName,
    TimeSpan DefaultConnectionTimeout,
    int DefaultMaximumReconnectAttempts,
    int OutputRetentionDays,
    int AuditRetentionDays,
    bool ClearSessionCredentialsOnDisconnect)
{
    public const int SchemaVersion = 1;

    public static ApplicationSettings Default { get; } = new(
        AppearancePreference.System,
        CultureName: null,
        TimeSpan.FromSeconds(15),
        DefaultMaximumReconnectAttempts: 2,
        OutputRetentionDays: 30,
        AuditRetentionDays: 90,
        ClearSessionCredentialsOnDisconnect: true);

    public static void Validate(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var cultureIsValid = settings.CultureName is null;
        if (!cultureIsValid)
        {
            try
            {
                _ = CultureInfo.GetCultureInfo(settings.CultureName!);
                cultureIsValid = true;
            }
            catch (CultureNotFoundException)
            {
            }
        }

        if (!Enum.IsDefined(settings.Appearance) ||
            !cultureIsValid ||
            settings.DefaultConnectionTimeout < TimeSpan.FromSeconds(1) ||
            settings.DefaultConnectionTimeout > TimeSpan.FromSeconds(120) ||
            settings.DefaultMaximumReconnectAttempts is < 0 or > 5 ||
            settings.OutputRetentionDays is < 1 or > 3650 ||
            settings.AuditRetentionDays is < 1 or > 3650 ||
            settings.OutputRetentionDays > settings.AuditRetentionDays)
        {
            throw new ArgumentException(
                "Application settings contain an invalid or unsafe value.",
                nameof(settings));
        }
    }
}

public sealed record ApplicationSettingsLoadResult(
    ApplicationSettings Settings,
    bool UsedDefaults,
    string? SafeDiagnostic = null);

public interface IApplicationSettingsRepository
{
    Task<ApplicationSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken = default);
}
