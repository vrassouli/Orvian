using System.Collections.Immutable;
using Orvian.Shell;
using Xunit;

namespace Orvian.Shell.Tests;

public sealed class DockerFeatureProjectionTests
{
    [Fact]
    public void ImagesAreProjectedAndSortedByReference()
    {
        var feature = Feature(
            ("Image 0001.ID", "sha256:z"),
            ("Image 0001.Repository", "zeta"),
            ("Image 0001.Tag", "latest"),
            ("Image 0001.Size", "10MB"),
            ("Image 0002.ID", "sha256:a"),
            ("Image 0002.Repository", "alpha"),
            ("Image 0002.Tag", "1.0"),
            ("Image 0002.Digest", "sha256:digest"));

        var images = DockerFeatureProjection.ParseImages(feature);

        Assert.Equal(2, images.Length);
        Assert.Equal("alpha:1.0", images[0].Reference);
        Assert.Equal("zeta:latest", images[1].Reference);
        Assert.Equal("sha256:digest", images[0].Digest);
    }

    [Fact]
    public void DanglingImageUsesIdAsReference()
    {
        var image = Assert.Single(DockerFeatureProjection.ParseImages(Feature(
            ("Image 0001.ID", "sha256:abc"),
            ("Image 0001.Repository", "<none>"),
            ("Image 0001.Tag", "<none>"))));

        Assert.Equal("sha256:abc", image.Reference);
    }

    [Fact]
    public void ContainersExposeStateAndStableActionIdentifier()
    {
        var containers = DockerFeatureProjection.ParseContainers(Feature(
            ("Container 0001.ID", "abc123"),
            ("Container 0001.Names", "web"),
            ("Container 0001.Image", "nginx:latest"),
            ("Container 0001.State", "running"),
            ("Container 0001.Status", "Up 2 hours"),
            ("Container 0001.Ports", "0.0.0.0:80->80/tcp")));

        var container = Assert.Single(containers);
        Assert.Equal("web", container.Identifier);
        Assert.Equal("nginx:latest", container.Image);
        Assert.True(container.IsRunning);
    }

    [Fact]
    public void UnnamedContainerFallsBackToId()
    {
        var container = Assert.Single(DockerFeatureProjection.ParseContainers(Feature(
            ("Container 0001.ID", "abc123"),
            ("Container 0001.State", "exited"))));

        Assert.Equal("abc123", container.Identifier);
        Assert.False(container.IsRunning);
    }

    private static PluginFeatureDisplay Feature(
        params (string Key, string Value)[] values) =>
        new(
            "Docker",
            string.Empty,
            Values:
            [
                .. values.Select(value =>
                    new PluginFeatureValue(value.Key, value.Key, value.Value))
            ],
            Mutations: ImmutableArray<PluginMutationAction>.Empty,
            ProviderId: "docker.cli.inventory");
}
