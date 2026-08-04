using System.Collections.Immutable;
using System.Net.Sockets;
using Orvian.Plugin.Abstractions;
using Orvian.Plugins.NetworkSettings;
using Xunit;

namespace Orvian.Plugins.NetworkSettings.Tests;

public sealed class NetworkSettingsProviderTests
{
    [Fact]
    public void ReadProviderParsesIpv4Ipv6RoutesAndDns()
    {
        var provider = new LinuxNetworkReadProvider();
        var result = provider.Parse(
        [
            Output("[{\"ifname\":\"eth0\",\"addr_info\":[{\"family\":\"inet\",\"local\":\"192.0.2.10\",\"prefixlen\":24},{\"family\":\"inet6\",\"local\":\"2001:db8::10\",\"prefixlen\":64}]}]"),
            Output("[{\"dst\":\"default\",\"gateway\":\"192.0.2.1\",\"dev\":\"eth0\"},{\"dst\":\"default\",\"gateway\":\"2001:db8::1\",\"dev\":\"eth0\"}]"),
            Output("nameserver 1.1.1.1\nnameserver 2606:4700:4700::1111\n")
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal("192.0.2.10/24", result.Model!.Values["eth0.ipv4_addresses"]);
        Assert.Equal("2001:db8::10/64", result.Model.Values["eth0.ipv6_addresses"]);
        Assert.Equal("192.0.2.1", result.Model.Values["eth0.ipv4_gateway"]);
        Assert.Equal("2001:db8::1", result.Model.Values["eth0.ipv6_gateway"]);
        Assert.Equal("1.1.1.1, 2606:4700:4700::1111", result.Model.Values["dns_servers"]);
    }

    [Fact]
    public void ReadProviderRejectsMalformedAndTruncatedOutput()
    {
        var provider = new LinuxNetworkReadProvider();
        Assert.False(provider.Parse([Output("{"), Output("[]"), Output("")]).IsSuccess);
        Assert.False(provider.Parse([Output("[]", truncated: true), Output("[]"), Output("")]).IsSuccess);
    }

    [Theory]
    [InlineData(false, "192.0.2.10/24", "192.0.2.1", "1.1.1.1,8.8.8.8")]
    [InlineData(true, "2001:db8::10/64", "2001:db8::1", "2606:4700:4700::1111")]
    public void MutationProviderBuildsStructuredArguments(
        bool ipv6, string address, string gateway, string dns)
    {
        var provider = new NetworkManagerMutationProvider(
            ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        var request = provider.CreateRequest(new Dictionary<string, string>
        {
            ["connection"] = "Production LAN",
            ["address"] = address,
            ["gateway"] = gateway,
            ["dns"] = dns
        });

        Assert.Equal("nmcli", request.Executable);
        Assert.Equal(PluginMutationRisk.Destructive, request.Risk);
        Assert.Contains(request.Arguments, item => item.Value == address);
        Assert.DoesNotContain(request.Arguments, item => item.Value.Contains(';'));
    }

    [Theory]
    [InlineData("192.0.2.10; reboot", "192.0.2.1", "1.1.1.1")]
    [InlineData("192.0.2.10/24", "2001:db8::1", "1.1.1.1")]
    [InlineData("192.0.2.10/24", "192.0.2.1", "1.1.1.1;id")]
    public void Ipv4MutationRejectsInvalidOrHostileValues(
        string address, string gateway, string dns)
    {
        var provider = new NetworkManagerMutationProvider(AddressFamily.InterNetwork);
        Assert.Throws<ArgumentException>(() => provider.CreateRequest(
            new Dictionary<string, string>
            {
                ["connection"] = "LAN",
                ["address"] = address,
                ["gateway"] = gateway,
                ["dns"] = dns
            }));
    }

    [Fact]
    public void NetplanMutationBuildsValidatedSetGenerateApplySequence()
    {
        var provider = new NetplanMutationProvider();
        var requests = provider.CreateRequests(new Dictionary<string, string>
        {
            ["interface"] = "enp0s5",
            ["ipv4Address"] = "192.0.2.10/24",
            ["ipv4Gateway"] = "192.0.2.1",
            ["ipv6Address"] = "2001:db8::10/64",
            ["ipv6Gateway"] = "2001:db8::1",
            ["dns"] = "1.1.1.1,2606:4700:4700::1111"
        });

        Assert.Equal(7, requests.Length);
        Assert.All(requests, request => Assert.Equal(PluginMutationRisk.Destructive, request.Risk));
        Assert.Equal("generate", requests[^2].Arguments[0].Value);
        Assert.Equal("apply", requests[^1].Arguments[0].Value);
        Assert.DoesNotContain(requests.SelectMany(request => request.Arguments),
            argument => argument.Value.Contains(';'));
    }

    [Theory]
    [InlineData("../../eth0", "192.0.2.10/24", "1.1.1.1")]
    [InlineData("eth0", "192.0.2.10/64", "1.1.1.1")]
    [InlineData("eth0", "192.0.2.10/24", "1.1.1.1;id")]
    public void NetplanMutationRejectsInvalidInput(
        string device, string address, string dns)
    {
        var provider = new NetplanMutationProvider();
        Assert.Throws<ArgumentException>(() => provider.CreateRequests(
            new Dictionary<string, string>
            {
                ["interface"] = device,
                ["ipv4Address"] = address,
                ["ipv4Gateway"] = "192.0.2.1",
                ["dns"] = dns
            }));
    }

    private static PluginCommandOutput Output(string value, bool truncated = false) =>
        new(0, value, string.Empty, truncated);
}
