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

public sealed class PluginReadFeatureServiceTests
{
    [Fact]
    public async Task DisconnectedHostDoesNotConstructOrExecuteCommand()
    {
        var provider = new FakeProvider();
        var commands = new RecordingExecutor();
        var service = CreateService(
            provider,
            commands,
            ConnectionState.Disconnected,
            ["time.read"]);

        var result = await service.ReadAsync("date-time", HostId);

        Assert.Equal(PluginFeatureReadStatus.Disconnected, result.Status);
        Assert.Equal(0, provider.CreateCount);
        Assert.Null(commands.Request);
    }

    [Fact]
    public async Task CompatibleProviderExecutesStructuredAuditedRequest()
    {
        var provider = new FakeProvider();
        var commands = new RecordingExecutor(Success("Timezone=UTC"));
        var service = CreateService(
            provider,
            commands,
            ConnectionState.Ready,
            ["time.read"]);

        var result = await service.ReadAsync("date-time", HostId);

        Assert.True(result.IsSuccess);
        Assert.Equal("provider.test", result.ProviderId);
        Assert.Equal("date", commands.Request!.Executable);
        Assert.Equal("+%z", commands.Request.Arguments[0].Value);
        Assert.Equal("plugin.test", commands.Request.PluginId);
        Assert.Equal(PluginPermissions.CommandReadExecute, commands.Request.RequiredPermission);
        Assert.Equal(InvocationSource.UserInterface, commands.Request.InvocationSource);
    }

    [Fact]
    public async Task MissingCapabilityReturnsUnsupportedWithoutExecution()
    {
        var commands = new RecordingExecutor();
        var service = CreateService(
            new FakeProvider(),
            commands,
            ConnectionState.Ready,
            ["shell.posix"]);

        var result = await service.ReadAsync("date-time", HostId);

        Assert.Equal(PluginFeatureReadStatus.Unsupported, result.Status);
        Assert.Null(commands.Request);
    }

    [Fact]
    public async Task TruncatedOutputIsNeverPassedToPluginParser()
    {
        var provider = new FakeProvider();
        var commands = new RecordingExecutor(Success("partial", truncated: true));
        var service = CreateService(
            provider,
            commands,
            ConnectionState.Ready,
            ["time.read"]);

        var result = await service.ReadAsync("date-time", HostId);

        Assert.Equal(PluginFeatureReadStatus.OutputTruncated, result.Status);
        Assert.Equal(0, provider.ParseCount);
    }

    [Fact]
    public async Task ProviderParserExceptionBecomesSafeFailure()
    {
        var provider = new FakeProvider { ThrowDuringParse = true };
        var service = CreateService(
            provider,
            new RecordingExecutor(Success("output")),
            ConnectionState.Ready,
            ["time.read"]);

        var result = await service.ReadAsync("date-time", HostId);

        Assert.Equal(PluginFeatureReadStatus.ParsingFailed, result.Status);
        Assert.DoesNotContain(
            "provider-secret-detail",
            result.SafeFailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiCommandProviderUsesAuditedPipelineForEveryRequest()
    {
        var provider = new FakeMultiProvider();
        var commands = new RecordingExecutor(Success("record"));
        var service = CreateService(
            provider,
            commands,
            ConnectionState.Ready,
            ["identity.getent"]);

        var result = await service.ReadAsync("users-groups", HostId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, commands.Requests.Count);
        Assert.Equal(
            commands.Requests[0].OperationId,
            commands.Requests[1].OperationId);
        Assert.All(
            commands.Requests,
            request => Assert.Equal(
                PluginPermissions.CommandReadExecute,
                request.RequiredPermission));
        Assert.Equal(1, provider.ParseCount);
    }

    private static readonly HostProfileId HostId = HostProfileId.New();

    private static PluginReadFeatureService CreateService(
        IReadOnlyFeatureProvider provider,
        RecordingExecutor commands,
        ConnectionState state,
        ImmutableHashSet<string> capabilities)
    {
        var catalog = new FakeCatalog(new(
            "plugin.test",
            new Version(1, 0),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                PluginPermissions.CommandReadExecute),
            provider));
        return new(
            catalog,
            new FakeDiscoverySnapshots(new(
                DiscoverySnapshotId.New(),
                HostId,
                Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                OperatingSystemFamily.Linux,
                ImmutableDictionary<string, DiscoveryFact>.Empty,
                capabilities,
                [])),
            new FakeConnections(state),
            commands);
    }

    private static CommandResult Success(string output, bool truncated = false) =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Status = CommandStatus.Succeeded,
            ExitCode = 0,
            StandardOutput = new(output, output.Length, truncated)
        };

    private sealed class FakeProvider : IReadOnlyFeatureProvider
    {
        public bool ThrowDuringParse { get; init; }

        public int CreateCount { get; private set; }

        public int ParseCount { get; private set; }

        public string FeatureId => "date-time";

        public string ProviderId => "provider.test";

        public int Priority => 10;

        public IReadOnlySet<string> RequiredCapabilities { get; } =
            ImmutableHashSet.Create(StringComparer.Ordinal, "time.read");

        public PluginReadRequest CreateRequest()
        {
            CreateCount++;
            return new("date", [new("+%z")], TimeSpan.FromSeconds(5));
        }

        public PluginFeatureParseResult Parse(PluginCommandOutput output)
        {
            ParseCount++;
            if (ThrowDuringParse)
            {
                throw new FormatException("provider-secret-detail");
            }

            return new(new(
                ImmutableDictionary.CreateRange(
                    StringComparer.Ordinal,
                    [KeyValuePair.Create("value", output.StandardOutput)])));
        }
    }

    private sealed class FakeMultiProvider : IMultiCommandReadFeatureProvider
    {
        public int ParseCount { get; private set; }
        public string FeatureId => "users-groups";
        public string ProviderId => "provider.multi";
        public int Priority => 10;
        public IReadOnlySet<string> RequiredCapabilities { get; } =
            ImmutableHashSet.Create(StringComparer.Ordinal, "identity.getent");

        public ImmutableArray<PluginReadRequest> CreateRequests() =>
        [
            new("getent", [new("passwd")], TimeSpan.FromSeconds(5)),
            new("getent", [new("group")], TimeSpan.FromSeconds(5))
        ];

        public PluginFeatureParseResult Parse(
            ImmutableArray<PluginCommandOutput> outputs)
        {
            ParseCount++;
            return new(new(
                ImmutableDictionary.CreateRange(
                    StringComparer.Ordinal,
                    [KeyValuePair.Create("outputs", outputs.Length.ToString())])));
        }
    }

    private sealed class FakeCatalog(RegisteredReadFeatureProvider provider)
        : IPluginFeatureCatalog
    {
        public ImmutableArray<RegisteredReadFeatureProvider> Snapshot => [provider];

        public ImmutableArray<RegisteredMutationFeatureProvider> MutationSnapshot => [];

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

    private sealed class FakeConnections(ConnectionState state)
        : IConnectionSnapshotProvider
    {
        public ConnectionSnapshot GetSnapshot(HostProfileId hostProfileId) =>
            new(
                hostProfileId,
                ConnectionId.New(),
                state,
                DateTimeOffset.UtcNow);
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

    private sealed class RecordingExecutor(CommandResult? result = null) : ICommandExecutor
    {
        public CommandRequest? Request { get; private set; }

        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> ExecuteAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            Requests.Add(request);
            return Task.FromResult(result ?? Success(string.Empty));
        }
    }
}
