using System.Collections.Immutable;
using System.Net;

namespace Orvian.Core.Hosts;

public readonly record struct HostProfileId(Guid Value)
{
    public static HostProfileId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public enum HostAuthenticationMethod
{
    Password,
    PrivateKey
}

public sealed record HostConnectionPreferences(
    TimeSpan ConnectionTimeout,
    int MaximumReconnectAttempts)
{
    public static HostConnectionPreferences Default { get; } =
        new(TimeSpan.FromSeconds(15), 2);
}

public sealed record HostProfile
{
    private HostProfile()
    {
    }

    public required HostProfileId Id { get; init; }

    public required string DisplayName { get; init; }

    public required string HostName { get; init; }

    public required int Port { get; init; }

    public required string UserName { get; init; }

    public required HostAuthenticationMethod AuthenticationMethod { get; init; }

    public string? CredentialSecretReference { get; init; }

    public string? PrivateKeyPath { get; init; }

    public ImmutableArray<string> Tags { get; init; } = [];

    public string? Notes { get; init; }

    public bool IsEnabled { get; init; } = true;

    public HostConnectionPreferences ConnectionPreferences { get; init; } =
        HostConnectionPreferences.Default;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public static HostProfileValidationResult Create(
        HostProfileId id,
        string displayName,
        string hostName,
        int port,
        string userName,
        HostAuthenticationMethod authenticationMethod,
        string? credentialSecretReference,
        IEnumerable<string>? tags,
        string? notes,
        bool isEnabled,
        HostConnectionPreferences? connectionPreferences,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? privateKeyPath = null)
    {
        var errors = Validate(
            id,
            displayName,
            hostName,
            port,
            userName,
            authenticationMethod,
            credentialSecretReference,
            privateKeyPath,
            tags,
            notes,
            connectionPreferences ?? HostConnectionPreferences.Default,
            createdAt,
            updatedAt);
        if (errors.Length > 0)
        {
            return new(null, errors);
        }

        var normalizedTags = (tags ?? [])
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new(
            new HostProfile
            {
                Id = id,
                DisplayName = displayName.Trim(),
                HostName = hostName.Trim(),
                Port = port,
                UserName = userName.Trim(),
                AuthenticationMethod = authenticationMethod,
                CredentialSecretReference = NullIfWhiteSpace(credentialSecretReference),
                PrivateKeyPath = NullIfWhiteSpace(privateKeyPath),
                Tags = normalizedTags,
                Notes = NullIfWhiteSpace(notes),
                IsEnabled = isEnabled,
                ConnectionPreferences =
                    connectionPreferences ?? HostConnectionPreferences.Default,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            },
            []);
    }

    private static ImmutableArray<HostProfileValidationError> Validate(
        HostProfileId id,
        string displayName,
        string hostName,
        int port,
        string userName,
        HostAuthenticationMethod authenticationMethod,
        string? credentialSecretReference,
        string? privateKeyPath,
        IEnumerable<string>? tags,
        string? notes,
        HostConnectionPreferences preferences,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var errors = ImmutableArray.CreateBuilder<HostProfileValidationError>();
        if (id.Value == Guid.Empty)
        {
            errors.Add(new("id.required", "Host profile ID is required."));
        }

        ValidateText(displayName, 200, "display_name", required: true, errors);
        ValidateHostName(hostName, errors);
        if (port is < 1 or > IPEndPoint.MaxPort)
        {
            errors.Add(new("port.range", "SSH port must be between 1 and 65535."));
        }

        ValidateText(userName, 256, "user_name", required: true, errors);
        ValidateText(
            credentialSecretReference,
            512,
            "credential_reference",
            required: false,
            errors);
        ValidateText(privateKeyPath, 4_096, "private_key_path", required: false, errors);
        if (authenticationMethod == HostAuthenticationMethod.PrivateKey &&
            string.IsNullOrWhiteSpace(privateKeyPath))
        {
            errors.Add(new(
                "private_key_path.required",
                "A private-key authentication profile requires a key path."));
        }

        if (authenticationMethod == HostAuthenticationMethod.Password &&
            !string.IsNullOrWhiteSpace(privateKeyPath))
        {
            errors.Add(new(
                "private_key_path.unexpected",
                "A password authentication profile cannot specify a private-key path."));
        }
        ValidateText(notes, 8_000, "notes", required: false, errors, allowNewLines: true);

        var materializedTags = (tags ?? []).ToArray();
        if (materializedTags.Length > 64)
        {
            errors.Add(new("tags.count", "A host profile can contain at most 64 tags."));
        }

        foreach (var tag in materializedTags)
        {
            ValidateText(tag, 100, "tag", required: true, errors);
        }

        if (preferences.ConnectionTimeout < TimeSpan.FromSeconds(1) ||
            preferences.ConnectionTimeout > TimeSpan.FromMinutes(2))
        {
            errors.Add(new(
                "connection_timeout.range",
                "Connection timeout must be between 1 second and 2 minutes."));
        }

        if (preferences.MaximumReconnectAttempts is < 0 or > 5)
        {
            errors.Add(new(
                "reconnect_attempts.range",
                "Reconnect attempts must be between 0 and 5."));
        }

        if (createdAt > updatedAt)
        {
            errors.Add(new(
                "timestamps.order",
                "Updated time cannot precede created time."));
        }

        return errors.ToImmutable();
    }

    private static void ValidateHostName(
        string hostName,
        ICollection<HostProfileValidationError> errors)
    {
        ValidateText(hostName, 253, "host_name", required: true, errors);
        if (!string.IsNullOrWhiteSpace(hostName) &&
            (hostName.Any(char.IsWhiteSpace) ||
             hostName.Contains('/') ||
             hostName.Contains('\\')))
        {
            errors.Add(new(
                "host_name.invalid",
                "Hostname or address contains invalid characters."));
        }
    }

    private static void ValidateText(
        string? value,
        int maximumLength,
        string field,
        bool required,
        ICollection<HostProfileValidationError> errors,
        bool allowNewLines = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                errors.Add(new($"{field}.required", $"{field} is required."));
            }

            return;
        }

        if (value.Length > maximumLength)
        {
            errors.Add(new(
                $"{field}.length",
                $"{field} exceeds its maximum length."));
        }

        if (value.Contains('\0') ||
            (!allowNewLines && (value.Contains('\r') || value.Contains('\n'))))
        {
            errors.Add(new(
                $"{field}.control_characters",
                $"{field} contains invalid control characters."));
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record HostProfileValidationError(string Code, string Message);

public sealed record HostProfileValidationResult(
    HostProfile? Profile,
    ImmutableArray<HostProfileValidationError> Errors)
{
    public bool IsSuccess => Profile is not null && Errors.IsEmpty;
}
