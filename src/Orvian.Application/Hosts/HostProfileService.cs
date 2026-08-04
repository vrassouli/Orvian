using System.Collections.Immutable;
using Orvian.Core.Hosts;
using Orvian.Security;

namespace Orvian.Application.Hosts;

public sealed record HostProfileInput(
    string DisplayName,
    string HostName,
    int Port,
    string UserName,
    HostAuthenticationMethod AuthenticationMethod,
    ImmutableArray<string> Tags,
    string? Notes,
    bool IsEnabled,
    HostConnectionPreferences ConnectionPreferences,
    string? PrivateKeyPath = null);

public sealed record CreateHostProfileRequest(
    HostProfileInput Profile,
    SecretValue? RememberedCredential = null);

public sealed record UpdateHostProfileRequest(
    HostProfileId Id,
    DateTimeOffset ExpectedUpdatedAt,
    HostProfileInput Profile,
    SecretValue? ReplacementRememberedCredential = null,
    bool RemoveRememberedCredential = false);

public sealed record HostProfileMutationWarning(string Code, string SafeMessage);

public sealed record HostProfileMutationResult(
    HostProfile? Profile,
    ImmutableArray<HostProfileMutationWarning> Warnings);

public sealed class HostProfileService
{
    private readonly IHostProfileRepository _profiles;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _timeProvider;

    public HostProfileService(
        IHostProfileRepository profiles,
        ISecretStore secrets,
        TimeProvider? timeProvider = null)
    {
        _profiles = profiles;
        _secrets = secrets;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<HostProfileMutationResult> CreateAsync(
        CreateHostProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);

        var id = HostProfileId.New();
        var now = _timeProvider.GetUtcNow();
        var secretReference = request.RememberedCredential is null
            ? (SecretReferenceId?)null
            : SecretReferenceId.New();
        var profile = BuildProfile(
            id,
            request.Profile,
            secretReference?.Value,
            now,
            now);

        if (secretReference is not null)
        {
            await StoreSecretAsync(
                profile,
                secretReference.Value,
                request.RememberedCredential!,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _profiles.AddAsync(profile, cancellationToken).ConfigureAwait(false);
            return new(profile, []);
        }
        catch
        {
            if (secretReference is not null)
            {
                try
                {
                    await _secrets.DeleteAsync(secretReference.Value, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new HostProfilePersistenceException(
                        "Host profile could not be saved and credential cleanup also failed.",
                        secretReference.Value,
                        cleanupFailure);
                }
            }

            throw;
        }
    }

    public async Task<HostProfileMutationResult> UpdateAsync(
        UpdateHostProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await _profiles.GetAsync(request.Id, cancellationToken)
            .ConfigureAwait(false) ??
            throw new InvalidOperationException("Host profile does not exist.");
        if (request.ReplacementRememberedCredential is not null &&
            request.RemoveRememberedCredential)
        {
            throw new ArgumentException(
                "Credential replacement and removal cannot be requested together.",
                nameof(request));
        }

        SecretReferenceId? newSecretReference =
            request.ReplacementRememberedCredential is null
                ? null
                : SecretReferenceId.New();
        var authenticationChanged =
            request.Profile.AuthenticationMethod != existing.AuthenticationMethod;
        var credentialReference =
            request.RemoveRememberedCredential ||
            (authenticationChanged && newSecretReference is null)
            ? null
            : newSecretReference?.Value ?? existing.CredentialSecretReference;
        var updated = BuildProfile(
            request.Id,
            request.Profile,
            credentialReference,
            existing.CreatedAt,
            _timeProvider.GetUtcNow());

        if (newSecretReference is not null)
        {
            await StoreSecretAsync(
                updated,
                newSecretReference.Value,
                request.ReplacementRememberedCredential!,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _profiles.UpdateAsync(
                updated,
                request.ExpectedUpdatedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (newSecretReference is not null)
            {
                await TryCompensateOrThrowAsync(newSecretReference.Value).ConfigureAwait(false);
            }

            throw;
        }

        var warnings = ImmutableArray.CreateBuilder<HostProfileMutationWarning>();
        if (existing.CredentialSecretReference is not null &&
            !string.Equals(
                existing.CredentialSecretReference,
                credentialReference,
                StringComparison.Ordinal))
        {
            try
            {
                await _secrets.DeleteAsync(
                    new(existing.CredentialSecretReference),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                warnings.Add(new(
                    "credential.cleanup_required",
                    "The host was updated, but its previous credential could not be removed."));
            }
        }

        return new(updated, warnings.ToImmutable());
    }

    public async Task<HostProfileMutationResult> DeleteAsync(
        HostProfileId id,
        CancellationToken cancellationToken = default)
    {
        var existing = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new(null, []);
        }

        await _profiles.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing.CredentialSecretReference is null)
        {
            return new(null, []);
        }

        try
        {
            await _secrets.DeleteAsync(
                new(existing.CredentialSecretReference),
                CancellationToken.None).ConfigureAwait(false);
            return new(null, []);
        }
        catch (Exception)
        {
            return new(
                null,
                [
                    new(
                        "credential.cleanup_required",
                        "The host was deleted, but its credential could not be removed.")
                ]);
        }
    }

    private static HostProfile BuildProfile(
        HostProfileId id,
        HostProfileInput input,
        string? credentialReference,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var validation = HostProfile.Create(
            id,
            input.DisplayName,
            input.HostName,
            input.Port,
            input.UserName,
            input.AuthenticationMethod,
            credentialReference,
            input.Tags,
            input.Notes,
            input.IsEnabled,
            input.ConnectionPreferences,
            createdAt,
            updatedAt,
            input.PrivateKeyPath);
        if (!validation.IsSuccess)
        {
            throw new HostProfileValidationException(validation.Errors);
        }

        return validation.Profile!;
    }

    private async Task StoreSecretAsync(
        HostProfile profile,
        SecretReferenceId secretReference,
        SecretValue secret,
        CancellationToken cancellationToken)
    {
        var kind = profile.AuthenticationMethod == HostAuthenticationMethod.Password
            ? SecretKind.SshPassword
            : SecretKind.PrivateKeyPassphrase;
        await _secrets.StoreAsync(
            new(secretReference, kind, profile.Id, profile.DisplayName),
            secret.Memory,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryCompensateOrThrowAsync(SecretReferenceId secretReference)
    {
        try
        {
            await _secrets.DeleteAsync(secretReference, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            throw new HostProfilePersistenceException(
                "Host profile could not be saved and credential cleanup also failed.",
                secretReference,
                cleanupFailure);
        }
    }
}

public sealed class HostProfileValidationException(
    ImmutableArray<HostProfileValidationError> errors)
    : Exception("Host profile validation failed.")
{
    public ImmutableArray<HostProfileValidationError> Errors { get; } = errors;
}

public sealed class HostProfilePersistenceException(
    string safeMessage,
    SecretReferenceId orphanedSecretReference,
    Exception innerException) : Exception(safeMessage, innerException)
{
    public SecretReferenceId OrphanedSecretReference { get; } = orphanedSecretReference;
}
