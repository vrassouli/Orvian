using Orvian.Core.Commands;
using Orvian.Core.Hosts;
using Xunit;

namespace Orvian.Discovery.Tests;

public sealed class HostDiscoveryServiceTests
{
    [Fact]
    public async Task Linux_systemd_facts_and_capabilities_are_normalized()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("Linux"),
            ["uname\u001f-r"] = Success("6.8.0"),
            ["uname\u001f-m"] = Success("x86_64"),
            ["hostname"] = Success("server-1"),
            ["cat\u001f/etc/os-release"] = Success(
                "ID=ubuntu\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"\n"),
            ["printenv\u001fSHELL"] = Success("/bin/bash"),
            ["id\u001f-u"] = Success("1000"),
            ["uptime\u001f-s"] = Success("2026-07-29 08:00:00"),
            ["systemctl\u001f--version"] = Success(
                "systemd 259 (259.5-0ubuntu3)\n+PAM +AUDIT +APPARMOR"),
            ["sudo\u001f--version"] = Success(
                "sudo-rs 0.2.13-0ubuntu1\nSudoers policy plugin version 1.9"),
            ["date\u001f+%s"] = Success("1785312000"),
            ["timedatectl\u001f--version"] = Success(
                "systemd 259 (259.5-0ubuntu3)\n+PAM +AUDIT +APPARMOR"),
            ["apt-get\u001f--version"] = Success(
                "apt 3.2.0 (arm64)\nSupported modules:\n*Ver: Standard .deb")
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.Equal(OperatingSystemFamily.Linux, snapshot.OperatingSystem);
        Assert.False(snapshot.IsPartial);
        Assert.Equal("x86_64", snapshot.Facts["kernel.arch"].Value);
        Assert.Equal("core.uname.architecture", snapshot.Facts["kernel.arch"].ProbeId);
        Assert.Contains("shell.posix", snapshot.Capabilities);
        Assert.Contains("init.systemd", snapshot.Capabilities);
        Assert.Contains("privilege.sudo", snapshot.Capabilities);
        Assert.Contains("package.apt", snapshot.Capabilities);
        Assert.Equal("Ubuntu 24.04 LTS", snapshot.Facts["os.pretty_name"].Value);
        Assert.Equal("apt", snapshot.Facts["package.provider"].Value);
        Assert.Contains(
            snapshot.ProbeOutcomes,
            outcome =>
                outcome.ProbeId == "core.openrc" &&
                outcome.Status == DiscoveryProbeStatus.Unsupported);
        Assert.All(
            executor.Requests,
            request => Assert.Equal(InvocationSource.Discovery, request.InvocationSource));
    }

    [Fact]
    public async Task Failed_probe_does_not_discard_successful_facts()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("FreeBSD"),
            ["uname\u001f-r"] = Failure(),
            ["uname\u001f-m"] = Success("amd64"),
            ["hostname"] = Success("bsd-host"),
            ["systemctl\u001f--version"] = Failure()
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.True(snapshot.IsPartial);
        Assert.Equal(OperatingSystemFamily.FreeBsd, snapshot.OperatingSystem);
        Assert.Equal("amd64", snapshot.Facts["kernel.arch"].Value);
        Assert.Contains(
            snapshot.ProbeOutcomes,
            outcome =>
                outcome.ProbeId == "core.uname.release" &&
                outcome.Status == DiscoveryProbeStatus.CommandFailed);
    }

    [Fact]
    public async Task Malformed_and_truncated_output_are_visible_not_parsed()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("Linux\nhostile"),
            ["uname\u001f-r"] = Success("6.8", truncated: true),
            ["uname\u001f-m"] = Success("arm64"),
            ["hostname"] = Success("host"),
            ["systemctl\u001f--version"] = Failure()
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.Equal(OperatingSystemFamily.Unknown, snapshot.OperatingSystem);
        Assert.DoesNotContain("kernel.release", snapshot.Facts.Keys);
        Assert.Contains(
            snapshot.ProbeOutcomes,
            outcome => outcome.Status == DiscoveryProbeStatus.ParsingFailed);
        Assert.Contains(
            snapshot.ProbeOutcomes,
            outcome => outcome.Status == DiscoveryProbeStatus.Truncated);
    }

    [Fact]
    public async Task Linux_openrc_fixture_selects_openrc_without_systemd()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("Linux"),
            ["uname\u001f-r"] = Success("6.6.0"),
            ["uname\u001f-m"] = Success("aarch64"),
            ["hostname"] = Success("openrc-host"),
            ["id\u001f-u"] = Success("1000"),
            ["id\u001f-Gn"] = Success("operator wheel"),
            ["rc-status\u001f--version"] = Success("OpenRC 0.55"),
            ["systemctl\u001f--version"] = Failure()
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.Equal(OperatingSystemFamily.Linux, snapshot.OperatingSystem);
        Assert.Contains("init.openrc", snapshot.Capabilities);
        Assert.DoesNotContain("init.systemd", snapshot.Capabilities);
        Assert.Contains("identity.groups", snapshot.Capabilities);
        Assert.Equal("operator wheel", snapshot.Facts["user.groups"].Value);
        Assert.False(snapshot.IsPartial);
    }

    [Fact]
    public async Task FreeBsd_fixture_discovers_version_rcd_and_pkg()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("FreeBSD"),
            ["uname\u001f-r"] = Success("14.2-RELEASE"),
            ["uname\u001f-m"] = Success("amd64"),
            ["hostname"] = Success("freebsd-host"),
            ["id\u001f-u"] = Success("0"),
            ["id\u001f-Gn"] = Success("wheel operator"),
            ["freebsd-version\u001f-u"] = Success("14.2-RELEASE-p1"),
            ["service\u001f-e"] = Success(
                "/etc/rc.d/sshd\n/usr/local/etc/rc.d/nginx\n"),
            ["pkg\u001f--version"] = Success("1.21.3")
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.Equal(OperatingSystemFamily.FreeBsd, snapshot.OperatingSystem);
        Assert.Equal("14.2-RELEASE-p1", snapshot.Facts["os.version"].Value);
        Assert.Contains("init.rcd", snapshot.Capabilities);
        Assert.Contains("package.pkg", snapshot.Capabilities);
        Assert.Contains("privilege.root", snapshot.Capabilities);
        Assert.False(snapshot.IsPartial);
    }

    [Fact]
    public async Task MacOs_fixture_discovers_sw_vers_launchd_and_brew()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("Darwin"),
            ["uname\u001f-r"] = Success("25.0.0"),
            ["uname\u001f-m"] = Success("arm64"),
            ["hostname"] = Success("mac-host"),
            ["id\u001f-u"] = Success("501"),
            ["id\u001f-Gn"] = Success("staff everyone"),
            ["sw_vers\u001f-productName"] = Success("macOS"),
            ["sw_vers\u001f-productVersion"] = Success("26.0"),
            ["launchctl\u001fversion"] = Success("Darwin Bootstrapper Version 7.0"),
            ["brew\u001f--version"] = Success("Homebrew 4.6.0")
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.Equal(OperatingSystemFamily.MacOs, snapshot.OperatingSystem);
        Assert.Equal("macOS", snapshot.Facts["os.pretty_name"].Value);
        Assert.Equal("26.0", snapshot.Facts["os.version"].Value);
        Assert.Contains("init.launchd", snapshot.Capabilities);
        Assert.Contains("package.brew", snapshot.Capabilities);
        Assert.False(snapshot.IsPartial);
    }

    [Fact]
    public async Task Malformed_rcd_inventory_does_not_grant_rcd_capability()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("FreeBSD"),
            ["uname\u001f-r"] = Success("14.2-RELEASE"),
            ["uname\u001f-m"] = Success("amd64"),
            ["hostname"] = Success("freebsd-host"),
            ["id\u001f-u"] = Success("1000"),
            ["service\u001f-e"] = Success("/tmp/untrusted-service\n")
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.DoesNotContain("init.rcd", snapshot.Capabilities);
        Assert.Contains(
            snapshot.ProbeOutcomes,
            outcome =>
                outcome.ProbeId == "core.rcd" &&
                outcome.Status == DiscoveryProbeStatus.ParsingFailed);
        Assert.True(snapshot.IsPartial);
    }

    [Fact]
    public async Task Malformed_or_oversized_tool_version_does_not_grant_capability()
    {
        var executor = new FixtureExecutor(new Dictionary<string, CommandResult>
        {
            ["uname\u001f-s"] = Success("Linux"),
            ["uname\u001f-r"] = Success("7.0"),
            ["uname\u001f-m"] = Success("aarch64"),
            ["hostname"] = Success("ubuntu-host"),
            ["id\u001f-u"] = Success("1000"),
            ["systemctl\u001f--version"] = Success("systemd\0hostile"),
            ["apt-get\u001f--version"] = Success(new string('x', (64 * 1024) + 1))
        });
        var service = new HostDiscoveryService(executor);

        var snapshot = await service.DiscoverAsync(Context());

        Assert.DoesNotContain("init.systemd", snapshot.Capabilities);
        Assert.DoesNotContain("package.apt", snapshot.Capabilities);
        Assert.Contains(
            snapshot.ProbeOutcomes,
            outcome =>
                outcome.ProbeId == "core.systemd" &&
                outcome.Status == DiscoveryProbeStatus.ParsingFailed);
        Assert.Contains(
            snapshot.ProbeOutcomes,
            outcome =>
                outcome.ProbeId == "core.apt" &&
                outcome.Status == DiscoveryProbeStatus.ParsingFailed);
    }

    [Fact]
    public void Duplicate_probe_ids_are_rejected()
    {
        var probe = new DiscoveryProbe(
            "duplicate",
            "uname",
            [],
            TimeSpan.FromSeconds(1),
            _ => new([], []));

        Assert.Throws<ArgumentException>(
            () => new HostDiscoveryService(
                new FixtureExecutor(new Dictionary<string, CommandResult>()),
                [probe, probe]));
    }

    [Fact]
    public async Task Pre_cancelled_discovery_stops_before_remote_execution()
    {
        var executor = new FixtureExecutor(
            new Dictionary<string, CommandResult>());
        var service = new HostDiscoveryService(executor);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var snapshot = await service.DiscoverAsync(
            Context(),
            cancellation.Token);

        Assert.Empty(executor.Requests);
        var outcome = Assert.Single(snapshot.ProbeOutcomes);
        Assert.Equal("core.uname.system", outcome.ProbeId);
        Assert.Equal(DiscoveryProbeStatus.Cancelled, outcome.Status);
        Assert.True(snapshot.IsPartial);
    }

    private static DiscoveryContext Context() =>
        new(HostProfileId.New(), Guid.NewGuid().ToString(), "orvian.discovery", new(0, 1));

    private static CommandResult Success(string output, bool truncated = false) =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Status = CommandStatus.Succeeded,
            ExitCode = 0,
            StandardOutput = new(output, output.Length, truncated)
        };

    private static CommandResult Failure() =>
        new()
        {
            CommandId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Status = CommandStatus.Failed,
            ExitCode = 127,
            FailureClassification = CommandFailureClassification.NonzeroExit,
            SafeFailureMessage = "Command is not available."
        };

    private sealed class FixtureExecutor(
        IReadOnlyDictionary<string, CommandResult> results) : ICommandExecutor
    {
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> ExecuteAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var key = string.Join(
                '\u001f',
                new[] { request.Executable }
                    .Concat(request.Arguments.Select(argument => argument.Value)));
            var configured = results.TryGetValue(key, out var result)
                ? result
                : Failure();
            return Task.FromResult(configured with { OperationId = request.OperationId });
        }
    }
}
