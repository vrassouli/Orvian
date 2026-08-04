using Orvian.Core.Hosts;
using Orvian.Security;
using Xunit;

namespace Orvian.Security.Tests;

public sealed class InMemorySecretStoreTests
{
    [Fact]
    public async Task Secrets_are_scoped_by_opaque_reference_and_returned_as_copies()
    {
        using var store = new InMemorySecretStore();
        var descriptor = Descriptor();
        await store.StoreAsync(descriptor, "secret".AsMemory());

        using var retrieved = await store.RetrieveAsync(descriptor.Id);
        var unknown = await store.RetrieveAsync(SecretReferenceId.New());

        Assert.Equal("secret", retrieved!.Memory.ToString());
        Assert.Null(unknown);
    }

    [Fact]
    public async Task Deleted_secret_is_unavailable()
    {
        using var store = new InMemorySecretStore();
        var descriptor = Descriptor();
        await store.StoreAsync(descriptor, "secret".AsMemory());

        await store.DeleteAsync(descriptor.Id);

        Assert.Null(await store.RetrieveAsync(descriptor.Id));
    }

    [Fact]
    public async Task Cancellation_prevents_secret_access()
    {
        using var store = new InMemorySecretStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.StoreAsync(
                Descriptor(),
                "secret".AsMemory(),
                cancellation.Token));
    }

    [Fact]
    public void Store_contract_does_not_expose_secret_enumeration()
    {
        var methodNames = typeof(ISecretStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(methodNames, name =>
            name.Contains("List", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Enumerate", StringComparison.OrdinalIgnoreCase));
    }

    private static SecretDescriptor Descriptor() =>
        new(
            SecretReferenceId.New(),
            SecretKind.SshPassword,
            HostProfileId.New(),
            "Test credential");
}
