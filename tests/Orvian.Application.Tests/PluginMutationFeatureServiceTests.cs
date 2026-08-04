using System.Collections.Immutable;
using Orvian.Application.Connections;
using Orvian.Application.Discovery;
using Orvian.Application.Features;
using Orvian.Connections;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Orvian.Plugin.Abstractions;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class PluginMutationFeatureServiceTests
{
    private static readonly HostProfileId HostId = HostProfileId.New();

    [Fact]
    public async Task CompatibleMutationBuildsElevatedStructuredOperation()
    {
        var operations = new RecordingOperations();
        var service = CreateService(
            ConnectionState.Ready,
            ["time.timezone.write"],
            operations);

        var result = await service.ExecuteAsync(
            "date-time",
            "timezone.change",
            HostId,
            new Dictionary<string, string> { ["timezone"] = "Europe/Berlin" });

        Assert.True(result.IsSuccess);
        var request = operations.Request!;
        Assert.Equal(OperationRisk.Elevated, request.Intent.Risk);
        Assert.Equal(PrivilegeLevel.Elevated, request.Intent.Privilege);
        Assert.Equal(PluginPermissions.CommandMutateExecute, request.Intent.RequiredPermission);
        var command = Assert.Single(request.Commands);
        Assert.Equal(CommandKind.Mutation, command.Kind);
        Assert.Equal("timedatectl", command.Executable);
        Assert.Equal(
            ["set-timezone", "Europe/Berlin"],
            command.Arguments.Select(argument => argument.Value));
    }

    [Fact]
    public async Task DisconnectedMutationDoesNotReachProviderOrCoordinator()
    {
        var provider = new FakeMutationProvider();
        var operations = new RecordingOperations();
        var service = CreateService(
            ConnectionState.Disconnected,
            ["time.timezone.write"],
            operations,
            provider);

        var result = await service.ExecuteAsync(
            "date-time",
            "timezone.change",
            HostId,
            new Dictionary<string, string> { ["timezone"] = "Europe/Berlin" });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, provider.CreateCount);
        Assert.Null(operations.Request);
    }

    [Fact]
    public async Task InvalidProviderInputBecomesSafeFailure()
    {
        var provider = new FakeMutationProvider { RejectInput = true };
        var operations = new RecordingOperations();
        var service = CreateService(
            ConnectionState.Ready,
            ["time.timezone.write"],
            operations,
            provider);

        var result = await service.ExecuteAsync(
            "date-time",
            "timezone.change",
            HostId,
            new Dictionary<string, string> { ["timezone"] = "bad" });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.SafeFailureMessage);
        Assert.DoesNotContain(
            "provider-internal-detail",
            result.SafeFailureMessage,
            StringComparison.Ordinal);
        Assert.Null(operations.Request);
    }

    [Fact]
    public async Task MissingWriteCapabilityHidesAndDeniesMutation()
    {
        var operations = new RecordingOperations();
        var service = CreateService(ConnectionState.Ready, ["time.read"], operations);

        var available = await service.GetAvailableAsync("date-time", HostId);
        var result = await service.ExecuteAsync(
            "date-time",
            "timezone.change",
            HostId,
            new Dictionary<string, string> { ["timezone"] = "Europe/Berlin" });

        Assert.Empty(available);
        Assert.False(result.IsSuccess);
        Assert.Null(operations.Request);
    }

    private static PluginMutationFeatureService CreateService(
        ConnectionState state,
        IEnumerable<string> capabilities,
        RecordingOperations operations,
        FakeMutationProvider? provider = null)
    {
        provider ??= new FakeMutationProvider();
        var registered = new RegisteredMutationFeatureProvider(
            "plugin.test",
            new Version(1, 0),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                PluginPermissions.CommandMutateExecute,
                PluginPermissions.PrivilegeElevatedRequest),
            provider);
        return new(
            new FakeCatalog(registered),
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
            new FakeConnections(state),
            operations);
    }

    private sealed class FakeMutationProvider : IMutationFeatureProvider
    {
        public bool RejectInput { get; init; }

        public int CreateCount { get; private set; }

        public string FeatureId => "date-time";
        public string MutationId => "timezone.change";
        public string ProviderId => "date-time.test";
        public int Priority => 10;
        public string Title => "Change timezone";
        public string Purpose => "Set the remote timezone.";
        public string ParameterName => "timezone";
        public string ParameterLabel => "Timezone";
        public PluginMutationRisk Risk => PluginMutationRisk.Elevated;
        public bool RequiresElevation => true;
        public IReadOnlySet<string> RequiredCapabilities { get; } =
            ImmutableHashSet.Create(StringComparer.Ordinal, "time.timezone.write");

        public PluginMutationRequest CreateRequest(
            IReadOnlyDictionary<string, string> parameters)
        {
            CreateCount++;
            if (RejectInput)
            {
                throw new ArgumentException("provider-internal-detail");
            }

            return new(
                Title,
                Purpose,
                Risk,
                "timedatectl",
                [new("set-timezone"), new(parameters["timezone"])],
                TimeSpan.FromSeconds(10));
        }
    }

    private sealed class FakeCatalog(RegisteredMutationFeatureProvider provider)
        : IPluginFeatureCatalog
    {
        public ImmutableArray<RegisteredReadFeatureProvider> Snapshot => [];
        public ImmutableArray<RegisteredMutationFeatureProvider> MutationSnapshot => [provider];

        public void Register(
            string pluginId,
            Version pluginVersion,
            IReadOnlySet<string> permissions,
            IEnumerable<IReadOnlyFeatureProvider> readProviders,
            IEnumerable<IMutationFeatureProvider> mutationProviders) =>
            throw new NotSupportedException();

        public void RemovePlugin(string pluginId) =>
            throw new NotSupportedException();
    }

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

    private sealed class FakeConnections(ConnectionState state)
        : IConnectionSnapshotProvider
    {
        public ConnectionSnapshot GetSnapshot(HostProfileId hostProfileId) =>
            new(hostProfileId, ConnectionId.New(), state, DateTimeOffset.UtcNow);
    }

    private sealed class RecordingOperations : IOperationCoordinator
    {
        public OperationRequest? Request { get; private set; }

        public Task<OperationResult> ExecuteAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(
                new OperationResult(
                    request.Intent.OperationId,
                    OperationStatus.Succeeded,
                    [
                        new CommandResult
                        {
                            CommandId = Guid.NewGuid(),
                            OperationId = request.Intent.OperationId,
                            Status = CommandStatus.Succeeded,
                            ExitCode = 0
                        }
                    ]));
        }
    }
}
