using System.Collections.Immutable;
using System.Text;
using Orvian.Connections;
using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Orvian.Discovery;
using Orvian.Plugin.Abstractions;
using Orvian.Plugins.DateTime;
using Orvian.Plugins.PackageInfo;
using Orvian.Plugins.Services;
using Orvian.Plugins.UsersGroups;
using Orvian.Security;
using Xunit;

namespace Orvian.Ssh.Tests;

public sealed class LiveSshTransportTests
{
    [LiveSshFact]
    [Trait("Category", "LiveSsh")]
    public async Task PasswordHostKeyAndStructuredCommandRoundTrip()
    {
        var host = RequiredEnvironment("ORVIAN_LIVE_SSH_HOST");
        var user = RequiredEnvironment("ORVIAN_LIVE_SSH_USER");
        var password = RequiredEnvironment("ORVIAN_LIVE_SSH_PASSWORD");
        var expectedFingerprint =
            RequiredEnvironment("ORVIAN_LIVE_SSH_FINGERPRINT");
        var port = int.TryParse(
            Environment.GetEnvironmentVariable("ORVIAN_LIVE_SSH_PORT"),
            out var configuredPort)
            ? configuredPort
            : 22;
        var now = DateTimeOffset.UtcNow;
        var profile = Assert.IsType<HostProfile>(HostProfile.Create(
            HostProfileId.New(),
            "Live SSH test host",
            host,
            port,
            user,
            HostAuthenticationMethod.Password,
            credentialSecretReference: null,
            tags: [],
            notes: null,
            isEnabled: true,
            new(TimeSpan.FromSeconds(10), MaximumReconnectAttempts: 0),
            now,
            now).Profile);
        var connectionId = ConnectionId.New();
        var factory = new SshNetConnectionFactory();

        using var firstAuthentication = CreatePasswordAuthentication(password);
        var firstAttempt = await factory.ConnectAsync(new(
            profile,
            connectionId,
            firstAuthentication,
            TrustedHostKey: null));

        Assert.Equal(
            ConnectionAttemptStatus.HostIdentityDecisionRequired,
            firstAttempt.Status);
        var observation = Assert.IsType<HostKeyObservation>(
            firstAttempt.ObservedHostKey);
        Assert.Equal(expectedFingerprint, observation.Sha256Fingerprint);
        Assert.Null(firstAttempt.Connection);

        var trusted = HostKeyVerifier.Trust(observation, DateTimeOffset.UtcNow);
        using var secondAuthentication = CreatePasswordAuthentication(password);
        var secondAttempt = await factory.ConnectAsync(new(
            profile,
            connectionId,
            secondAuthentication,
            trusted));
        Assert.Equal(ConnectionAttemptStatus.Connected, secondAttempt.Status);
        await using var connection = Assert.IsAssignableFrom<IRemoteConnection>(
            secondAttempt.Connection);

        var result = await connection.ExecuteAsync(
            Guid.NewGuid(),
            new CommandRequest
            {
                OperationId = Guid.NewGuid(),
                HostProfileId = profile.Id.ToString(),
                ConnectionId = connectionId.ToString(),
                PluginId = "orvian.live-test",
                PluginVersion = new Version(0, 1),
                RequiredPermission = "command.read.execute",
                Executable = "uname",
                Arguments = ImmutableArray.Create(new CommandArgument("-s")),
                Kind = CommandKind.ReadOnly,
                InvocationSource = InvocationSource.UserInterface,
                OutputLogging = OutputLoggingMode.MetadataOnly,
                Timeout = TimeSpan.FromSeconds(5)
            },
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Linux", result.StandardOutput.Content.Trim());
        Assert.False(result.StandardOutput.IsTruncated);
        Assert.True(result.StandardOutput.ObservedBytes >= 6);

        var capabilities = await VerifyDiscoveryAsync(connection, profile, connectionId);
        Assert.Contains("init.systemd", capabilities);
        Assert.Contains("service.manage", capabilities);
        Assert.Contains("identity.getent", capabilities);
        Assert.Contains("time.systemd", capabilities);
        Assert.Contains("package.apt", capabilities);

        await VerifyProviderAsync(
            connection,
            profile,
            connectionId,
            new SystemdDateTimeProvider());
        await VerifyProviderAsync(
            connection,
            profile,
            connectionId,
            new SystemdServicesReadProvider());
        await VerifyProviderAsync(
            connection,
            profile,
            connectionId,
            new AptPackageInfoProvider());
        await VerifyMultiCommandProviderAsync(
            connection,
            profile,
            connectionId,
            new GetentUsersGroupsProvider());
        await connection.DisconnectAsync();
        Assert.False(connection.IsConnected);
    }

    private static async Task<ImmutableHashSet<string>> VerifyDiscoveryAsync(
        IRemoteConnection connection,
        HostProfile profile,
        ConnectionId connectionId)
    {
        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var probe in StandardDiscoveryProbes.Create())
        {
            var result = await ExecuteAsync(
                connection,
                profile,
                connectionId,
                probe.Executable,
                probe.Arguments,
                probe.Timeout);
            if (result.ExitCode != 0)
            {
                Assert.True(probe.IsOptional, $"Required probe '{probe.Id}' failed.");
                continue;
            }

            Assert.False(result.StandardOutput.IsTruncated);
            var parsed = probe.Parse(result.StandardOutput.Content);
            Assert.True(
                parsed.IsSuccess,
                $"Probe '{probe.Id}' failed to parse: {parsed.SafeFailureMessage}");
            foreach (var fact in parsed.Facts)
            {
                facts.TryAdd(fact.Key, fact.Value);
            }

            capabilities.UnionWith(parsed.Capabilities);
        }

        Assert.Equal("linux", facts["os.family"]);
        Assert.Equal("1000", facts["user.effective_uid"]);
        return capabilities.ToImmutable();
    }

    private static async Task VerifyProviderAsync(
        IRemoteConnection connection,
        HostProfile profile,
        ConnectionId connectionId,
        IReadOnlyFeatureProvider provider)
    {
        var request = provider.CreateRequest();
        var result = await ExecuteAsync(
            connection,
            profile,
            connectionId,
            request.Executable,
            [.. request.Arguments.Select(argument =>
                new CommandArgument(argument.Value, argument.IsSensitive))],
            request.Timeout);
        var parsed = provider.Parse(ToPluginOutput(result));
        Assert.True(
            parsed.IsSuccess,
            $"Provider '{provider.ProviderId}' failed: {parsed.SafeFailureMessage}");
        Assert.NotEmpty(parsed.Model!.Values);
    }

    private static async Task VerifyMultiCommandProviderAsync(
        IRemoteConnection connection,
        HostProfile profile,
        ConnectionId connectionId,
        IMultiCommandReadFeatureProvider provider)
    {
        var outputs = ImmutableArray.CreateBuilder<PluginCommandOutput>();
        foreach (var request in provider.CreateRequests())
        {
            var result = await ExecuteAsync(
                connection,
                profile,
                connectionId,
                request.Executable,
                [.. request.Arguments.Select(argument =>
                    new CommandArgument(argument.Value, argument.IsSensitive))],
                request.Timeout);
            outputs.Add(ToPluginOutput(result));
        }

        var parsed = provider.Parse(outputs.ToImmutable());
        Assert.True(
            parsed.IsSuccess,
            $"Provider '{provider.ProviderId}' failed: {parsed.SafeFailureMessage}");
        Assert.NotEmpty(parsed.Model!.Values);
    }

    private static async Task<TransportCommandResult> ExecuteAsync(
        IRemoteConnection connection,
        HostProfile profile,
        ConnectionId connectionId,
        string executable,
        ImmutableArray<CommandArgument> arguments,
        TimeSpan timeout) =>
        await connection.ExecuteAsync(
            Guid.NewGuid(),
            new CommandRequest
            {
                OperationId = Guid.NewGuid(),
                HostProfileId = profile.Id.ToString(),
                ConnectionId = connectionId.ToString(),
                PluginId = "orvian.live-test",
                PluginVersion = new Version(0, 1),
                RequiredPermission = "command.read.execute",
                Executable = executable,
                Arguments = arguments,
                Kind = CommandKind.ReadOnly,
                InvocationSource = InvocationSource.UserInterface,
                OutputLogging = OutputLoggingMode.MetadataOnly,
                Timeout = timeout
            },
            CancellationToken.None);

    private static PluginCommandOutput ToPluginOutput(TransportCommandResult result) =>
        new(
            result.ExitCode,
            result.StandardOutput.Content,
            result.StandardError.Content,
            result.StandardOutput.IsTruncated || result.StandardError.IsTruncated);

    private static PasswordConnectionAuthentication CreatePasswordAuthentication(
        string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return new(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) ??
        throw new InvalidOperationException(
            $"Live SSH test variable '{name}' was not configured.");
}

public sealed class LiveSshFactAttribute : FactAttribute
{
    public LiveSshFactAttribute()
    {
        var required = new[]
        {
            "ORVIAN_LIVE_SSH_HOST",
            "ORVIAN_LIVE_SSH_USER",
            "ORVIAN_LIVE_SSH_PASSWORD",
            "ORVIAN_LIVE_SSH_FINGERPRINT"
        };
        if (required.Any(name =>
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
        {
            Skip =
                "Set the ORVIAN_LIVE_SSH_* variables to run the opt-in live transport test.";
        }
    }
}
