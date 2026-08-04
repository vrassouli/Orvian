using Orvian.Plugin.Abstractions;
using Orvian.Plugins.Services;
using Xunit;

namespace Orvian.Plugins.Services.Tests;

public sealed class ServicesProviderTests
{
    [Fact]
    public void ReadRequestUsesStructuredBoundedSystemctlInvocation()
    {
        var request = new SystemdServicesReadProvider().CreateRequest();

        Assert.Equal("systemctl", request.Executable);
        Assert.Contains(request.Arguments, argument => argument.Value == "--type=service");
        Assert.Contains(request.Arguments, argument => argument.Value == "--no-pager");
        Assert.True(request.Timeout <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ServiceInventoryParsesNormalizedRecords()
    {
        var output =
            """
            Id=nginx.service
            Description=A high performance web server
            LoadState=loaded
            ActiveState=active
            SubState=running
            UnitFileState=enabled

            Id=sshd.service
            Description=OpenSSH server
            LoadState=loaded
            ActiveState=inactive
            SubState=dead
            UnitFileState=disabled

            """;

        var result = new SystemdServicesReadProvider().Parse(
            new(0, output, string.Empty, false));

        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Model!.Values["Service count"]);
        Assert.Equal("nginx.service", result.Model.Values["Service 0001.Id"]);
        Assert.Equal("active", result.Model.Values["Service 0001.ActiveState"]);
        Assert.Equal("enabled", result.Model.Values["Service 0001.UnitFileState"]);
        Assert.Equal("sshd.service", result.Model.Values["Service 0002.Id"]);
        Assert.Equal("inactive", result.Model.Values["Service 0002.ActiveState"]);
        Assert.Equal("dead", result.Model.Values["Service 0002.SubState"]);
        Assert.Equal("disabled", result.Model.Values["Service 0002.UnitFileState"]);
    }

    [Theory]
    [InlineData("Id=nginx.service\nActiveState=active\nSubState=running\n")]
    [InlineData("Id=nginx.service\nId=duplicate.service\nLoadState=loaded\nActiveState=active\nSubState=running\n")]
    [InlineData("Unknown=value\n")]
    [InlineData("Id=not-a-service\nLoadState=loaded\nActiveState=active\nSubState=running\n")]
    public void MalformedServiceInventoryFailsSafely(string output)
    {
        var result = new SystemdServicesReadProvider().Parse(
            new(0, output, string.Empty, false));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.SafeFailureMessage);
    }

    [Fact]
    public void TruncatedInventoryIsNeverAccepted()
    {
        var result = new SystemdServicesReadProvider().Parse(
            new(0, "Id=nginx.service", string.Empty, true));

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("start", PluginMutationRisk.Low)]
    [InlineData("stop", PluginMutationRisk.Elevated)]
    [InlineData("restart", PluginMutationRisk.Elevated)]
    [InlineData("enable", PluginMutationRisk.Low)]
    [InlineData("disable", PluginMutationRisk.Low)]
    public void MutationUsesValidatedServiceAsSeparateArgument(
        string action,
        PluginMutationRisk risk)
    {
        var request = new SystemdServiceMutationProvider(action, risk).CreateRequest(
            new Dictionary<string, string> { ["service"] = "nginx.service" });

        Assert.Equal("systemctl", request.Executable);
        Assert.Equal(
            [action, "nginx.service"],
            request.Arguments.Select(argument => argument.Value));
        Assert.Equal(risk, request.Risk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nginx")]
    [InlineData("../nginx.service")]
    [InlineData("nginx.service; reboot")]
    [InlineData("nginx.service/other")]
    public void MutationRejectsUnsafeServiceIdentifier(string service)
    {
        Assert.Throws<ArgumentException>(
            () => new SystemdServiceMutationProvider(
                "restart",
                PluginMutationRisk.Elevated).CreateRequest(
                new Dictionary<string, string> { ["service"] = service }));
    }
}
