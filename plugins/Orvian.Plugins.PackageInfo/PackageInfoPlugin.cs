using System.Collections.Immutable;
using System.Globalization;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.PackageInfo;

public sealed class PackageInfoPlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.package-info",
        "Package Information",
        new Version(0, 1, 0),
        "Remotune",
        "Inspect bounded installed-package information.",
        new Version(0, 1, 0),
        [
            PluginPermissions.UiNavigationContribute,
            PluginPermissions.CommandReadExecute
        ]);

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Navigation.AddFeaturePage(
            "/package-information",
            "Package Information",
            "archive",
            typeof(PackageInfoPageViewModel),
            "package-info");
        builder.Features.Add(new AptPackageInfoProvider());
        builder.Features.Add(new DnfPackageInfoProvider());
        builder.Features.Add(new YumPackageInfoProvider());
        builder.Features.Add(new PacmanPackageInfoProvider());
        builder.Features.Add(new ZypperPackageInfoProvider());
        builder.Features.Add(new ApkPackageInfoProvider());
        builder.Features.Add(new PkgPackageInfoProvider());
        builder.Features.Add(new BrewPackageInfoProvider());
    }
}

public sealed class PackageInfoPageViewModel;

public abstract class PackageInfoProvider(
    string providerId,
    string requiredCapability,
    string executable,
    ImmutableArray<PluginCommandArgument> arguments) : IReadOnlyFeatureProvider
{
    private const int MaximumOutputLength = 1024 * 1024;
    private const int MaximumParsedPackages = 10000;
    private const int MaximumDisplayedPackages = 200;
    private readonly ImmutableHashSet<string> _capabilities =
        ImmutableHashSet.Create(StringComparer.Ordinal, requiredCapability);

    public string FeatureId => "package-info";

    public string ProviderId { get; } = providerId;

    public int Priority => 100;

    public IReadOnlySet<string> RequiredCapabilities => _capabilities;

    public PluginReadRequest CreateRequest() =>
        new(executable, arguments, TimeSpan.FromSeconds(30));

    public PluginFeatureParseResult Parse(PluginCommandOutput output)
    {
        if (output.IsTruncated || output.ExitCode != 0)
        {
            return PluginFeatureParseResult.Failure(
                "Installed-package output was incomplete.");
        }

        if (output.StandardOutput.Length > MaximumOutputLength ||
            output.StandardOutput.Contains('\0'))
        {
            return PluginFeatureParseResult.Failure(
                "Installed-package output was malformed.");
        }

        var packages = new List<string>();
        foreach (var rawLine in output.StandardOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (packages.Count >= MaximumParsedPackages ||
                line.Length > 4096 ||
                line.Any(character =>
                    character != '\t' && char.IsControl(character)))
            {
                return PluginFeatureParseResult.Failure(
                    "Installed-package output exceeded its safe record bounds.");
            }

            packages.Add(NormalizeLine(line));
        }

        if (packages.Count == 0 || packages.Any(string.IsNullOrWhiteSpace))
        {
            return PluginFeatureParseResult.Failure(
                "No valid installed-package records were returned.");
        }

        packages.Sort(StringComparer.OrdinalIgnoreCase);
        var displayed = Math.Min(packages.Count, MaximumDisplayedPackages);
        var values = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.Ordinal);
        values.Add(
            "Installed package count",
            packages.Count.ToString(CultureInfo.InvariantCulture));
        values.Add(
            "Displayed package count",
            displayed.ToString(CultureInfo.InvariantCulture));
        for (var index = 0; index < displayed; index++)
        {
            values.Add($"Package {index + 1:D4}", packages[index]);
        }

        if (displayed < packages.Count)
        {
            values.Add(
                "Display notice",
                $"Showing the first {displayed} packages in normalized sort order.");
        }

        return new(new PluginFeatureReadModel(values.ToImmutable()));
    }

    protected virtual string NormalizeLine(string line) =>
        line.Replace('\t', ' ');
}

public sealed class AptPackageInfoProvider() : PackageInfoProvider(
    "package-info.apt",
    "package.apt",
    "dpkg-query",
    [
        new("-W"),
        new("-f=${binary:Package}\\t${Version}\\n")
    ]);

public sealed class DnfPackageInfoProvider() : PackageInfoProvider(
    "package-info.rpm",
    "package.dnf",
    "rpm",
    [
        new("-qa"),
        new("--qf"),
        new("%{NAME}\\t%{VERSION}-%{RELEASE}.%{ARCH}\\n")
    ]);

public sealed class YumPackageInfoProvider() : PackageInfoProvider(
    "package-info.yum-rpm",
    "package.yum",
    "rpm",
    [
        new("-qa"),
        new("--qf"),
        new("%{NAME}\\t%{VERSION}-%{RELEASE}.%{ARCH}\\n")
    ]);

public sealed class PacmanPackageInfoProvider() : PackageInfoProvider(
    "package-info.pacman",
    "package.pacman",
    "pacman",
    [new("-Q")]);

public sealed class ZypperPackageInfoProvider() : PackageInfoProvider(
    "package-info.zypper-rpm",
    "package.zypper",
    "rpm",
    [
        new("-qa"),
        new("--qf"),
        new("%{NAME}\\t%{VERSION}-%{RELEASE}.%{ARCH}\\n")
    ]);

public sealed class ApkPackageInfoProvider() : PackageInfoProvider(
    "package-info.apk",
    "package.apk",
    "apk",
    [new("info"), new("-v")]);

public sealed class PkgPackageInfoProvider() : PackageInfoProvider(
    "package-info.pkg",
    "package.pkg",
    "pkg",
    [new("info")]);

public sealed class BrewPackageInfoProvider() : PackageInfoProvider(
    "package-info.brew",
    "package.brew",
    "brew",
    [new("list"), new("--versions")]);
