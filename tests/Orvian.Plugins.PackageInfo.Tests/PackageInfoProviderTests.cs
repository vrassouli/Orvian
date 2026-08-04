using Orvian.Plugin.Abstractions;
using Orvian.Plugins.PackageInfo;
using Xunit;

namespace Orvian.Plugins.PackageInfo.Tests;

public sealed class PackageInfoProviderTests
{
    public static TheoryData<IReadOnlyFeatureProvider, string, string> Providers => new()
    {
        { new AptPackageInfoProvider(), "package.apt", "dpkg-query" },
        { new DnfPackageInfoProvider(), "package.dnf", "rpm" },
        { new YumPackageInfoProvider(), "package.yum", "rpm" },
        { new PacmanPackageInfoProvider(), "package.pacman", "pacman" },
        { new ZypperPackageInfoProvider(), "package.zypper", "rpm" },
        { new ApkPackageInfoProvider(), "package.apk", "apk" },
        { new PkgPackageInfoProvider(), "package.pkg", "pkg" },
        { new BrewPackageInfoProvider(), "package.brew", "brew" }
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void ProviderUsesExpectedCapabilityAndStructuredExecutable(
        IReadOnlyFeatureProvider provider,
        string capability,
        string executable)
    {
        var request = provider.CreateRequest();

        Assert.Contains(capability, provider.RequiredCapabilities);
        Assert.Equal(executable, request.Executable);
        Assert.NotEmpty(request.Arguments);
        Assert.True(request.Timeout <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void PackageRecordsAreBoundedSortedAndNormalized()
    {
        var result = new AptPackageInfoProvider().Parse(
            new(
                0,
                "zlib1g\t1.2.13\napt\t2.7.14\n",
                string.Empty,
                false));

        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Model!.Values["Installed package count"]);
        Assert.Equal("apt 2.7.14", result.Model.Values["Package 0001"]);
        Assert.Equal("zlib1g 1.2.13", result.Model.Values["Package 0002"]);
    }

    [Theory]
    [InlineData(1, "package failed", false)]
    [InlineData(0, "partial", true)]
    [InlineData(0, "", false)]
    [InlineData(0, "unsafe\u0001value", false)]
    public void InvalidOrIncompletePackageOutputFailsSafely(
        int exitCode,
        string output,
        bool truncated)
    {
        var result = new AptPackageInfoProvider().Parse(
            new(exitCode, output, string.Empty, truncated));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.SafeFailureMessage);
    }

    [Fact]
    public void DisplayIsCappedWithoutTreatingCompleteOutputAsTruncated()
    {
        var output = string.Join(
            '\n',
            Enumerable.Range(1, 250).Select(index => $"package-{index}\t1.0"));

        var result = new AptPackageInfoProvider().Parse(
            new(0, output, string.Empty, false));

        Assert.True(result.IsSuccess);
        Assert.Equal("250", result.Model!.Values["Installed package count"]);
        Assert.Equal("200", result.Model.Values["Displayed package count"]);
        Assert.Contains("Display notice", result.Model.Values.Keys);
    }
}
