using System.Collections.Immutable;
using Orvian.Application.Discovery;
using Orvian.Application.Execution;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Orvian.Security;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class CapabilityPrivilegeCommandPreparerTests
{
    private static readonly HostProfileId HostId = HostProfileId.New();

    [Fact]
    public async Task RootCapabilityUsesOriginalStructuredRequest()
    {
        using var credentials = new SessionPrivilegeCredentialCache();
        var preparer = CreatePreparer(credentials, "privilege.root");
        var request = Request();

        var result = await preparer.PrepareAsync(request);

        Assert.True(result.IsAllowed);
        Assert.Same(request, result.TransportRequest);
        Assert.Null(result.TransportRequest!.StandardInput);
    }

    [Fact]
    public async Task SudoWithoutCredentialUsesNonInteractiveMode()
    {
        using var credentials = new SessionPrivilegeCredentialCache();
        var result = await CreatePreparer(credentials, "privilege.sudo")
            .PrepareAsync(Request());

        Assert.True(result.IsAllowed);
        Assert.Equal("sudo", result.TransportRequest!.Executable);
        Assert.Equal(
            ["-n", "--", "systemctl", "restart", "nginx"],
            result.TransportRequest.Arguments.Select(argument => argument.Value));
        Assert.Null(result.TransportRequest.StandardInput);
    }

    [Fact]
    public async Task SudoCredentialIsOnlyPlacedInDisposableStandardInput()
    {
        using var credentials = new SessionPrivilegeCredentialCache();
        credentials.Store(HostId, "p@ss word");

        var result = await CreatePreparer(credentials, "privilege.sudo")
            .PrepareAsync(Request());

        Assert.True(result.IsAllowed);
        Assert.Equal(
            ["-S", "-p", "", "--", "systemctl", "restart", "nginx"],
            result.TransportRequest!.Arguments.Select(argument => argument.Value));
        Assert.DoesNotContain(
            result.TransportRequest.Arguments,
            argument => argument.Value.Contains("p@ss", StringComparison.Ordinal));
        Assert.Equal(
            "p@ss word\n",
            new string(result.TransportRequest.StandardInput!.Memory.Span));

        result.TransportRequest.StandardInput.Dispose();
        Assert.Throws<ObjectDisposedException>(
            () => result.TransportRequest.StandardInput.Memory.ToString());
    }

    [Fact]
    public async Task DoasUsesNonInteractiveStructuredWrapper()
    {
        using var credentials = new SessionPrivilegeCredentialCache();
        var result = await CreatePreparer(credentials, "privilege.doas")
            .PrepareAsync(Request());

        Assert.True(result.IsAllowed);
        Assert.Equal("doas", result.TransportRequest!.Executable);
        Assert.Equal(
            ["-n", "systemctl", "restart", "nginx"],
            result.TransportRequest.Arguments.Select(argument => argument.Value));
    }

    [Fact]
    public async Task MissingProviderFailsClosed()
    {
        using var credentials = new SessionPrivilegeCredentialCache();
        var result = await CreatePreparer(credentials)
            .PrepareAsync(Request());

        Assert.False(result.IsAllowed);
        Assert.Null(result.TransportRequest);
        Assert.NotNull(result.SafeFailureMessage);
    }

    private static CapabilityPrivilegeCommandPreparer CreatePreparer(
        IPrivilegeCredentialCache credentials,
        params string[] capabilities) =>
        new(
            new FakeDiscoverySnapshots(
                new(
                    DiscoverySnapshotId.New(),
                    HostId,
                    Guid.NewGuid().ToString(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    OperatingSystemFamily.Linux,
                    ImmutableDictionary<string, DiscoveryFact>.Empty,
                    capabilities.ToImmutableHashSet(StringComparer.Ordinal),
                    [])),
            credentials);

    private static CommandRequest Request() =>
        new()
        {
            OperationId = Guid.NewGuid(),
            HostProfileId = HostId.ToString(),
            ConnectionId = Guid.NewGuid().ToString(),
            PluginId = "orvian.test",
            PluginVersion = new Version(1, 0),
            RequiredPermission = "command.mutate.execute",
            Executable = "systemctl",
            Arguments = [new("restart"), new("nginx")],
            Kind = CommandKind.Mutation,
            Privilege = PrivilegeLevel.Elevated,
            Reason = "Restart the selected service."
        };

    private sealed class FakeDiscoverySnapshots(DiscoverySnapshot snapshot)
        : IDiscoverySnapshotRepository
    {
        public Task StoreAsync(
            DiscoverySnapshot value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscoverySnapshot?> GetLatestAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DiscoverySnapshot?>(snapshot);
    }
}
