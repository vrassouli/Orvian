using Orvian.Application.Hosts;
using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class HostProfileServiceTests
{
    [Fact]
    public async Task Create_stores_secret_first_and_persists_only_opaque_reference()
    {
        var events = new List<string>();
        var repository = new FakeRepository(events);
        var secrets = new FakeSecretStore(events);
        var service = new HostProfileService(repository, secrets);
        using var credential = new SecretValue("password".AsSpan());

        var result = await service.CreateAsync(new(Input(), credential));

        Assert.Equal(["secret-store", "profile-add"], events);
        Assert.NotNull(result.Profile!.CredentialSecretReference);
        Assert.DoesNotContain("password", result.Profile.CredentialSecretReference);
        Assert.Equal(
            result.Profile.CredentialSecretReference,
            secrets.StoredDescriptor!.Id.Value);
    }

    [Fact]
    public async Task Persistence_failure_removes_new_secret()
    {
        var events = new List<string>();
        var repository = new FakeRepository(events) { FailAdd = true };
        var secrets = new FakeSecretStore(events);
        var service = new HostProfileService(repository, secrets);
        using var credential = new SecretValue("password".AsSpan());

        await Assert.ThrowsAsync<IOException>(
            () => service.CreateAsync(new(Input(), credential)));

        Assert.Equal(["secret-store", "profile-add", "secret-delete"], events);
        Assert.Null(secrets.StoredDescriptor);
    }

    [Fact]
    public async Task Failed_compensation_surfaces_orphan_reference_without_secret_value()
    {
        var repository = new FakeRepository([]) { FailAdd = true };
        var secrets = new FakeSecretStore([]) { FailDelete = true };
        var service = new HostProfileService(repository, secrets);
        using var credential = new SecretValue("do-not-leak".AsSpan());

        var exception = await Assert.ThrowsAsync<HostProfilePersistenceException>(
            () => service.CreateAsync(new(Input(), credential)));

        Assert.DoesNotContain("do-not-leak", exception.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("secret-", exception.OrphanedSecretReference.Value);
    }

    [Fact]
    public async Task Invalid_profile_never_touches_secret_store_or_repository()
    {
        var events = new List<string>();
        var service = new HostProfileService(
            new FakeRepository(events),
            new FakeSecretStore(events));
        using var credential = new SecretValue("password".AsSpan());

        await Assert.ThrowsAsync<HostProfileValidationException>(
            () => service.CreateAsync(new(
                Input() with { HostName = "bad\nhost" },
                credential)));

        Assert.Empty(events);
    }

    [Fact]
    public async Task Successful_update_warns_when_old_secret_cleanup_fails()
    {
        var events = new List<string>();
        var existing = CreateExisting("secret-old");
        var repository = new FakeRepository(events) { Profile = existing };
        var secrets = new FakeSecretStore(events) { FailDelete = true };
        var service = new HostProfileService(repository, secrets);
        using var replacement = new SecretValue("new-password".AsSpan());

        var result = await service.UpdateAsync(new(
            existing.Id,
            existing.UpdatedAt,
            Input() with { DisplayName = "Updated" },
            replacement));

        Assert.Equal("Updated", result.Profile!.DisplayName);
        Assert.Single(result.Warnings);
        Assert.Equal("credential.cleanup_required", result.Warnings[0].Code);
    }

    [Fact]
    public async Task Authentication_method_change_does_not_reuse_old_secret_kind()
    {
        var events = new List<string>();
        var existing = CreateExisting("secret-old");
        var repository = new FakeRepository(events) { Profile = existing };
        var secrets = new FakeSecretStore(events);
        var service = new HostProfileService(repository, secrets);

        var result = await service.UpdateAsync(new(
            existing.Id,
            existing.UpdatedAt,
            Input() with
            {
                AuthenticationMethod = HostAuthenticationMethod.PrivateKey,
                PrivateKeyPath = "/keys/id_ed25519"
            }));

        Assert.Null(result.Profile!.CredentialSecretReference);
        Assert.Contains("secret-delete", events);
    }

    [Fact]
    public async Task Explicit_removal_clears_reference_and_deletes_remembered_secret()
    {
        var events = new List<string>();
        var existing = CreateExisting("secret-old");
        var repository = new FakeRepository(events) { Profile = existing };
        var secrets = new FakeSecretStore(events);
        var service = new HostProfileService(repository, secrets);

        var result = await service.UpdateAsync(new(
            existing.Id,
            existing.UpdatedAt,
            Input(),
            RemoveRememberedCredential: true));

        Assert.Null(result.Profile!.CredentialSecretReference);
        Assert.Equal(["profile-update", "secret-delete"], events);
    }

    private static HostProfileInput Input() =>
        new(
            "Server",
            "server.example.test",
            22,
            "operator",
            HostAuthenticationMethod.Password,
            [],
            null,
            true,
            HostConnectionPreferences.Default);

    private static HostProfile CreateExisting(string? credentialReference)
    {
        var now = DateTimeOffset.UtcNow;
        return Assert.IsType<HostProfile>(
            HostProfile.Create(
                HostProfileId.New(),
                "Server",
                "server.example.test",
                22,
                "operator",
                HostAuthenticationMethod.Password,
                credentialReference,
                [],
                null,
                true,
                HostConnectionPreferences.Default,
                now,
                now).Profile);
    }

    private sealed class FakeRepository(List<string> events) : IHostProfileRepository
    {
        public HostProfile? Profile { get; set; }

        public bool FailAdd { get; set; }

        public Task<HostProfile?> GetAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Profile);

        public Task<HostProfilePage> SearchAsync(
            HostProfileSearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AddAsync(
            HostProfile profile,
            CancellationToken cancellationToken = default)
        {
            events.Add("profile-add");
            if (FailAdd)
            {
                return Task.FromException(new IOException("Simulated persistence failure."));
            }

            Profile = profile;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(
            HostProfile profile,
            DateTimeOffset expectedUpdatedAt,
            CancellationToken cancellationToken = default)
        {
            events.Add("profile-update");
            Profile = profile;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default)
        {
            events.Add("profile-delete");
            Profile = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSecretStore(List<string> events) : ISecretStore
    {
        public SecretDescriptor? StoredDescriptor { get; private set; }

        public bool FailDelete { get; set; }

        public Task StoreAsync(
            SecretDescriptor descriptor,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            events.Add("secret-store");
            StoredDescriptor = descriptor;
            return Task.CompletedTask;
        }

        public Task<SecretValue?> RetrieveAsync(
            SecretReferenceId id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SecretValue?>(null);

        public Task DeleteAsync(
            SecretReferenceId id,
            CancellationToken cancellationToken = default)
        {
            events.Add("secret-delete");
            if (FailDelete)
            {
                return Task.FromException(new IOException("Simulated cleanup failure."));
            }

            StoredDescriptor = null;
            return Task.CompletedTask;
        }
    }
}
