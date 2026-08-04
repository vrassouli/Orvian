using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class WindowsCredentialManagerSecretStoreTests
{
    [Fact]
    public async Task StoreRetrieveAndDeleteUseOpaqueTargetAndClearBuffers()
    {
        var api = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialManagerSecretStore(api);
        var descriptor = Descriptor();

        await store.StoreAsync(descriptor, "pässword".AsMemory());
        Assert.All(api.LastInputBuffer!, value => Assert.Equal(0, value));
        using var retrieved = await store.RetrieveAsync(descriptor.Id);
        Assert.All(api.LastOutputBuffer!, value => Assert.Equal(0, value));
        await store.DeleteAsync(descriptor.Id);

        Assert.Equal($"Orvian/{descriptor.Id.Value}", api.LastTarget);
        Assert.Equal("pässword", retrieved!.Memory.ToString());
        Assert.Null(await store.RetrieveAsync(descriptor.Id));
    }

    [Fact]
    public async Task BackendFailureDoesNotExposeSecret()
    {
        var api = new FakeWindowsCredentialApi { FailureStatus = 5 };
        var store = new WindowsCredentialManagerSecretStore(api);

        var exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.StoreAsync(Descriptor(), "do-not-leak".AsMemory()));

        Assert.Equal(5, exception.Status);
        Assert.DoesNotContain(
            "do-not-leak",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.All(api.LastInputBuffer!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CredentialManagerSizeLimitFailsBeforeNativeAccess()
    {
        var api = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialManagerSecretStore(api);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreAsync(Descriptor(), new string('x', 2561).AsMemory()));

        Assert.Equal(0, api.CallCount);
    }

    [Fact]
    public async Task NativeCredentialManagerRoundTripsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialManagerSecretStore();
        var descriptor = Descriptor();
        try
        {
            await store.StoreAsync(descriptor, "orvian-native-test".AsMemory());
            using var retrieved = await store.RetrieveAsync(descriptor.Id);

            Assert.Equal("orvian-native-test", retrieved!.Memory.ToString());
        }
        finally
        {
            await store.DeleteAsync(descriptor.Id);
        }
    }

    private static SecretDescriptor Descriptor() =>
        new(
            SecretReferenceId.New(),
            SecretKind.SshPassword,
            HostProfileId.New(),
            "Windows test credential");

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi
    {
        private readonly Dictionary<string, byte[]> _values = [];

        public int CallCount { get; private set; }
        public int? FailureStatus { get; init; }
        public string? LastTarget { get; private set; }
        public byte[]? LastInputBuffer { get; private set; }
        public byte[]? LastOutputBuffer { get; private set; }

        public void Upsert(string target, byte[] secret)
        {
            Track(target);
            LastInputBuffer = secret;
            ThrowIfConfigured();
            _values[target] = (byte[])secret.Clone();
        }

        public byte[]? Find(string target)
        {
            Track(target);
            ThrowIfConfigured();
            if (!_values.TryGetValue(target, out var value))
            {
                return null;
            }

            LastOutputBuffer = (byte[])value.Clone();
            return LastOutputBuffer;
        }

        public void Delete(string target)
        {
            Track(target);
            ThrowIfConfigured();
            _values.Remove(target);
        }

        private void Track(string target)
        {
            CallCount++;
            LastTarget = target;
        }

        private void ThrowIfConfigured()
        {
            if (FailureStatus is { } status)
            {
                throw new SecretStoreException("access", status);
            }
        }
    }
}
