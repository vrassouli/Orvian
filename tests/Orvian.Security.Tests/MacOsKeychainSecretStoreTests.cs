using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class MacOsKeychainSecretStoreTests
{
    [Fact]
    public async Task StoreRetrieveAndDeleteUseOpaqueAccount()
    {
        var api = new FakeKeychainApi();
        var store = new MacOsKeychainSecretStore(api);
        var id = SecretReferenceId.New();
        var descriptor = new SecretDescriptor(
            id,
            SecretKind.SshPassword,
            HostProfileId.New(),
            "Production host");

        await store.StoreAsync(descriptor, "pässword".AsMemory());
        using var retrieved = await store.RetrieveAsync(id);
        await store.DeleteAsync(id);

        Assert.Equal("io.orvian.credentials", api.LastService);
        Assert.Equal(id.Value, api.LastAccount);
        Assert.Equal("pässword", retrieved!.Memory.ToString());
        Assert.Null(await store.RetrieveAsync(id));
    }

    [Fact]
    public async Task CancellationPreventsKeychainAccess()
    {
        var api = new FakeKeychainApi();
        var store = new MacOsKeychainSecretStore(api);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.RetrieveAsync(
                new SecretReferenceId("secret-cancelled"),
                cancellation.Token));

        Assert.Equal(0, api.CallCount);
    }

    [Fact]
    public async Task NativeFailureDoesNotExposeSecret()
    {
        var api = new FakeKeychainApi { FailureStatus = -42 };
        var store = new MacOsKeychainSecretStore(api);
        var descriptor = new SecretDescriptor(
            SecretReferenceId.New(),
            SecretKind.SshPassword,
            HostProfileId.New(),
            "Host");

        var exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.StoreAsync(descriptor, "do-not-leak".AsMemory()));

        Assert.Equal(-42, exception.Status);
        Assert.DoesNotContain("do-not-leak", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeKeychainApi : IMacKeychainApi
    {
        private readonly Dictionary<string, byte[]> _values = [];

        public int CallCount { get; private set; }

        public int? FailureStatus { get; init; }

        public string? LastService { get; private set; }

        public string? LastAccount { get; private set; }

        public byte[]? Find(string service, string account)
        {
            Track(service, account);
            ThrowIfConfigured();
            return _values.TryGetValue(account, out var value)
                ? (byte[])value.Clone()
                : null;
        }

        public void Upsert(string service, string account, byte[] secret)
        {
            Track(service, account);
            ThrowIfConfigured();
            _values[account] = (byte[])secret.Clone();
        }

        public void Delete(string service, string account)
        {
            Track(service, account);
            ThrowIfConfigured();
            _values.Remove(account);
        }

        private void Track(string service, string account)
        {
            CallCount++;
            LastService = service;
            LastAccount = account;
        }

        private void ThrowIfConfigured()
        {
            if (FailureStatus is { } status)
            {
                throw new SecretStoreException("store", status);
            }
        }
    }
}
