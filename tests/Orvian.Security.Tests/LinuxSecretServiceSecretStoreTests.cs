using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class LinuxSecretServiceSecretStoreTests
{
    [Fact]
    public async Task StoreRetrieveAndDeleteUseOpaqueAttributesAndClearBuffers()
    {
        var api = new FakeLinuxSecretServiceApi();
        var store = new LinuxSecretServiceSecretStore(api);
        var descriptor = Descriptor();

        await store.StoreAsync(descriptor, "line one\npässword".AsMemory());
        Assert.All(api.LastInputBuffer!, value => Assert.Equal(0, value));
        using var retrieved = await store.RetrieveAsync(descriptor.Id);
        Assert.All(api.LastOutputBuffer!, value => Assert.Equal(0, value));
        await store.DeleteAsync(descriptor.Id);

        Assert.Equal(descriptor.Id.Value, api.LastReference);
        Assert.Equal("Orvian credential", api.LastLabel);
        Assert.Equal("line one\npässword", retrieved!.Memory.ToString());
        Assert.Null(await store.RetrieveAsync(descriptor.Id));
    }

    [Fact]
    public async Task CancellationAndFailureClearInputWithoutLeakingSecret()
    {
        var api = new FakeLinuxSecretServiceApi { FailureStatus = 7 };
        var store = new LinuxSecretServiceSecretStore(api);

        var exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.StoreAsync(Descriptor(), "do-not-leak".AsMemory()));

        Assert.DoesNotContain(
            "do-not-leak",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.All(api.LastInputBuffer!, value => Assert.Equal(0, value));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.RetrieveAsync(
                SecretReferenceId.New(),
                cancellation.Token));
    }

    [Fact]
    public async Task SecretToolSizeLimitFailsBeforeBackendAccess()
    {
        var api = new FakeLinuxSecretServiceApi();
        var store = new LinuxSecretServiceSecretStore(api);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreAsync(Descriptor(), new string('x', 8192).AsMemory()));

        Assert.Equal(0, api.CallCount);
    }

    private static SecretDescriptor Descriptor() =>
        new(
            SecretReferenceId.New(),
            SecretKind.PrivateKeyPassphrase,
            HostProfileId.New(),
            "Linux test credential");

    private sealed class FakeLinuxSecretServiceApi : ILinuxSecretServiceApi
    {
        private readonly Dictionary<string, byte[]> _values = [];

        public int CallCount { get; private set; }
        public int? FailureStatus { get; init; }
        public string? LastReference { get; private set; }
        public string? LastLabel { get; private set; }
        public byte[]? LastInputBuffer { get; private set; }
        public byte[]? LastOutputBuffer { get; private set; }

        public Task StoreAsync(
            string reference,
            string label,
            byte[] secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Track(reference);
            LastLabel = label;
            LastInputBuffer = secret;
            ThrowIfConfigured();
            _values[reference] = (byte[])secret.Clone();
            return Task.CompletedTask;
        }

        public Task<byte[]?> LookupAsync(
            string reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Track(reference);
            ThrowIfConfigured();
            if (!_values.TryGetValue(reference, out var value))
            {
                return Task.FromResult<byte[]?>(null);
            }

            LastOutputBuffer = (byte[])value.Clone();
            return Task.FromResult<byte[]?>(LastOutputBuffer);
        }

        public Task ClearAsync(
            string reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Track(reference);
            ThrowIfConfigured();
            _values.Remove(reference);
            return Task.CompletedTask;
        }

        private void Track(string reference)
        {
            CallCount++;
            LastReference = reference;
        }

        private void ThrowIfConfigured()
        {
            if (FailureStatus is { } status)
            {
                throw new SecretStoreException("access Secret Service", status);
            }
        }
    }
}
