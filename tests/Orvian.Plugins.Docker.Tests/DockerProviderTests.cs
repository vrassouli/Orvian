using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;
using Orvian.Plugins.Docker;
using Xunit;

namespace Orvian.Plugins.Docker.Tests;

public sealed class DockerProviderTests
{
    [Fact]
    public void MutationPluginDeclaresRegistrationPermissions()
    {
        var plugin = new DockerPlugin();

        Assert.Contains(PluginPermissions.CommandMutateExecute, plugin.Manifest.Permissions);
        Assert.Contains(PluginPermissions.PrivilegeElevatedRequest, plugin.Manifest.Permissions);
    }

    [Fact]
    public void InventoryUsesStructuredBoundedDockerCommands()
    {
        var requests = new DockerInventoryProvider().CreateRequests();

        Assert.Equal(2, requests.Length);
        Assert.All(requests, request => Assert.Equal("docker", request.Executable));
        Assert.All(requests, request => Assert.True(request.Timeout <= TimeSpan.FromSeconds(30)));
        Assert.Equal(
            "{{json .ID}}\t{{json .Repository}}\t{{json .Tag}}\t" +
            "{{json .Digest}}\t{{json .CreatedSince}}\t{{json .Size}}",
            requests[0].Arguments[^1].Value);
    }

    [Fact]
    public void InventoryParsesImageAndContainerJsonLines()
    {
        var outputs = ImmutableArray.Create(
            new PluginCommandOutput(0,
                "\"sha256:abc\"\t\"nginx\"\t\"latest\"\t\"<none>\"\t\"2 days ago\"\t\"192MB\"\n",
                string.Empty, false),
            new PluginCommandOutput(0,
                "\"0123456789ab\"\t\"web\"\t\"nginx:latest\"\t\"running\"\t\"Up 1 hour\"\t\"80/tcp\"\t\"1kB\"\t\"2026-08-01 12:00:00 +0000 UTC\"\n",
                string.Empty, false));

        var result = new DockerInventoryProvider().Parse(outputs);

        Assert.True(result.IsSuccess);
        Assert.Equal("1", result.Model!.Values["Image count"]);
        Assert.Equal("nginx", result.Model.Values["Image 0001.Repository"]);
        Assert.Equal("1", result.Model.Values["Container count"]);
        Assert.Equal("web", result.Model.Values["Container 0001.Names"]);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("\"sha256:abc\"\t\"nginx\"")]
    [InlineData("42\t\"nginx\"\t\"latest\"\t\"digest\"\t\"today\"\t\"1MB\"")]
    public void MalformedInventoryFailsSafely(string imageOutput)
    {
        var result = new DockerInventoryProvider().Parse(
        [
            new(0, imageOutput, string.Empty, false),
            new(0, string.Empty, string.Empty, false)
        ]);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.SafeFailureMessage);
    }

    [Fact]
    public void TruncatedInventoryIsRejected()
    {
        var result = new DockerInventoryProvider().Parse(
        [
            new(0, string.Empty, string.Empty, true),
            new(0, string.Empty, string.Empty, false)
        ]);

        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("container.start", "container", "start")]
    [InlineData("container.stop", "container", "stop")]
    [InlineData("container.restart", "container", "restart")]
    [InlineData("container.remove", "container", "rm")]
    public void ContainerActionUsesValidatedIdentifierAsSeparateArgument(
        string mutationId,
        string group,
        string action)
    {
        var provider = Providers().Single(item => item.MutationId == mutationId);

        var request = provider.CreateRequest(
            new Dictionary<string, string> { ["container"] = "web-01" });

        Assert.Equal("docker", request.Executable);
        Assert.Equal(group, request.Arguments[0].Value);
        Assert.Equal(action, request.Arguments[1].Value);
        Assert.Equal("web-01", request.Arguments[^1].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("web; reboot")]
    [InlineData("../web")]
    [InlineData("web name")]
    public void ContainerActionRejectsHostileIdentifier(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            DockerMutationProvider.StopContainer().CreateRequest(
                new Dictionary<string, string> { ["container"] = value }));
    }

    [Theory]
    [InlineData("nginx:latest")]
    [InlineData("registry.example.test/team/api@sha256:abcdef0123456789")]
    public void PullImageAcceptsStructuredImageReference(string image)
    {
        var request = DockerMutationProvider.PullImage().CreateRequest(
            new Dictionary<string, string> { ["image"] = image });

        Assert.Equal(["image", "pull", image], request.Arguments.Select(item => item.Value));
    }

    [Theory]
    [InlineData("--quiet")]
    [InlineData("nginx; reboot")]
    [InlineData("nginx latest")]
    public void PullImageRejectsHostileReference(string image)
    {
        Assert.Throws<ArgumentException>(() =>
            DockerMutationProvider.PullImage().CreateRequest(
                new Dictionary<string, string> { ["image"] = image }));
    }

    [Fact]
    public void PruneIsNonInteractiveAndDestructive()
    {
        var provider = DockerMutationProvider.PruneSystem();
        var request = provider.CreateRequest(new Dictionary<string, string>());

        Assert.Equal(PluginMutationRisk.Destructive, provider.Risk);
        Assert.Equal(
            ["system", "prune", "--force"],
            request.Arguments.Select(argument => argument.Value));
    }

    [Fact]
    public void ContainerLogsUsesParameterizedBoundedReadRequest()
    {
        var provider = new DockerContainerLogsProvider();
        var request = provider.CreateRequest(
            new Dictionary<string, string> { ["container"] = "web" });

        Assert.Equal("container.logs", provider.ActionId);
        Assert.Equal("docker", request.Executable);
        Assert.Equal(
            ["container", "logs", "--tail", "200", "web"],
            request.Arguments.Select(argument => argument.Value));
        Assert.True(request.Timeout <= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void ContainerLogsCombinesStandardStreams()
    {
        var result = new DockerContainerLogsProvider().Parse(
            new(0, "stdout line", "stderr line", false));

        Assert.True(result.IsSuccess);
        Assert.Contains("stdout line", result.Model!.Values["Logs"]);
        Assert.Contains("stderr line", result.Model.Values["Logs"]);
    }

    [Theory]
    [InlineData("--follow")]
    [InlineData("web; reboot")]
    [InlineData("../web")]
    public void ContainerLogsRejectsHostileIdentifier(string container)
    {
        Assert.Throws<ArgumentException>(() =>
            new DockerContainerLogsProvider().CreateRequest(
                new Dictionary<string, string> { ["container"] = container }));
    }

    private static IEnumerable<DockerMutationProvider> Providers() =>
    [
        DockerMutationProvider.StartContainer(),
        DockerMutationProvider.StopContainer(),
        DockerMutationProvider.RestartContainer(),
        DockerMutationProvider.RemoveContainer()
    ];
}
