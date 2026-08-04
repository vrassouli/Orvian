using System.Collections.Immutable;
using Orvian.Auditing;
using Orvian.Application.Connections;
using Orvian.Application.Discovery;
using Orvian.Application.Features;
using Orvian.Application.Hosts;
using Orvian.Connections;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Orvian.Plugin.Abstractions;
using Xunit;

namespace Orvian.Application.Tests;

public sealed class PluginLocalHostFeatureServiceTests
{
    private static readonly HostProfileId HostId = HostProfileId.New();
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-29T12:00:00Z");

    [Fact]
    public async Task ReadBuildsSanitizedContextWithoutRemoteExecution()
    {
        var provider = new RecordingProvider();
        var service = CreateService(provider, CreateProfile(), CreateDiscovery());

        var result = await service.ReadAsync("host-overview", HostId);

        Assert.True(result.IsSuccess);
        Assert.Equal("local.test", result.Model!.Values["host.name"]);
        var context = Assert.IsType<LocalHostFeatureContext>(provider.Context);
        Assert.Equal("secret-ref-must-not-cross", CreateProfile().CredentialSecretReference);
        Assert.Equal("Production", context.DisplayName);
        Assert.Equal("prod.example:2222", context.Endpoint);
        Assert.DoesNotContain("operator", context.Facts.Values);
        Assert.Equal(Now, context.GeneratedAt);
        Assert.Equal("SHA256:trusted", context.TrustedHostKeyFingerprint);
    }

    [Fact]
    public async Task MissingProfileReturnsSafeUnsupportedResult()
    {
        var service = CreateService(new RecordingProvider(), null, null);

        var result = await service.ReadAsync("host-overview", HostId);

        Assert.Equal(PluginFeatureReadStatus.Unsupported, result.Status);
        Assert.Equal(
            "The selected host profile no longer exists.",
            result.SafeFailureMessage);
    }

    [Fact]
    public async Task ProviderFailureIsContainedAsSafeParsingFailure()
    {
        var service = CreateService(
            new RecordingProvider(throwOnBuild: true),
            CreateProfile(),
            CreateDiscovery());

        var result = await service.ReadAsync("host-overview", HostId);

        Assert.Equal(PluginFeatureReadStatus.ParsingFailed, result.Status);
        Assert.Equal(
            "The local feature provider could not build a safe view.",
            result.SafeFailureMessage);
    }

    private static PluginLocalHostFeatureService CreateService(
        ILocalHostFeatureProvider provider,
        HostProfile? profile,
        DiscoverySnapshot? discovery)
    {
        var registered = new RegisteredLocalHostFeatureProvider(
            "orvian.host-overview",
            new Version(0, 1),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                PluginPermissions.HostRead),
            provider);
        return new(
            new FakeCatalog(registered),
            new FakeProfiles(profile),
            new FakeDiscovery(discovery),
            new FakeTrustedKeys(new(
                HostId,
                "prod.example",
                2222,
                "ssh-ed25519",
                "SHA256:trusted",
                Now.AddDays(-1))),
            new FakeConnections(),
            new FixedTimeProvider(Now));
    }

    private static HostProfile CreateProfile() =>
        Assert.IsType<HostProfile>(HostProfile.Create(
            HostId,
            "Production",
            "prod.example",
            2222,
            "operator",
            HostAuthenticationMethod.Password,
            "secret-ref-must-not-cross",
            ["production"],
            "private note",
            true,
            HostConnectionPreferences.Default,
            Now.AddDays(-2),
            Now.AddDays(-1)).Profile);

    private static DiscoverySnapshot CreateDiscovery() =>
        new(
            DiscoverySnapshotId.New(),
            HostId,
            "connection",
            Now.AddMinutes(-2),
            Now.AddMinutes(-1),
            OperatingSystemFamily.Linux,
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                new Dictionary<string, DiscoveryFact>
                {
                    ["host.name"] = new(
                        "host.name",
                        "local.test",
                        "system",
                        Now.AddMinutes(-1))
                }),
            ImmutableHashSet.Create(StringComparer.Ordinal, "os.linux"),
            [new("system", DiscoveryProbeStatus.Succeeded)]);

    private sealed class RecordingProvider(bool throwOnBuild = false)
        : ILocalHostFeatureProvider
    {
        public string FeatureId => "host-overview";
        public string ProviderId => "host-overview.test";
        public int Priority => 1;
        public IReadOnlySet<string> RequiredCapabilities =>
            ImmutableHashSet<string>.Empty;
        public LocalHostFeatureContext? Context { get; private set; }

        public PluginFeatureParseResult Build(LocalHostFeatureContext context)
        {
            Context = context;
            if (throwOnBuild)
            {
                throw new InvalidOperationException("Sensitive internal exception.");
            }

            return new(new(context.Facts));
        }
    }

    private sealed class FakeCatalog(RegisteredLocalHostFeatureProvider provider)
        : IPluginFeatureCatalog
    {
        public ImmutableArray<RegisteredLocalHostFeatureProvider> LocalHostSnapshot =>
            [provider];
        public ImmutableArray<RegisteredReadFeatureProvider> Snapshot => [];
        public ImmutableArray<RegisteredMutationFeatureProvider> MutationSnapshot => [];

        public void Register(
            string pluginId,
            Version pluginVersion,
            IReadOnlySet<string> permissions,
            IEnumerable<IReadOnlyFeatureProvider> readProviders,
            IEnumerable<IMutationFeatureProvider> mutationProviders) =>
            throw new NotSupportedException();

        public void RemovePlugin(string pluginId) => throw new NotSupportedException();
    }

    private sealed class FakeProfiles(HostProfile? profile) : IHostProfileRepository
    {
        public Task<HostProfile?> GetAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
        public Task<HostProfilePage> SearchAsync(
            HostProfileSearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddAsync(HostProfile value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task UpdateAsync(
            HostProfile value,
            DateTimeOffset expectedUpdatedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(
            HostProfileId id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDiscovery(DiscoverySnapshot? snapshot)
        : IDiscoverySnapshotRepository
    {
        public Task StoreAsync(
            DiscoverySnapshot value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<DiscoverySnapshot?> GetLatestAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FakeTrustedKeys(TrustedHostKey? key)
        : ITrustedHostKeyRepository
    {
        public Task<TrustedHostKey?> GetAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(key);
        public Task StoreAsync(
            TrustedHostKey trustedHostKey,
            HostTrustAuditEvent auditEvent,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(
            HostProfileId hostProfileId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConnections : IConnectionSnapshotProvider
    {
        public ConnectionSnapshot GetSnapshot(HostProfileId hostProfileId) =>
            new(hostProfileId, ConnectionId.New(), ConnectionState.Ready, Now);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
