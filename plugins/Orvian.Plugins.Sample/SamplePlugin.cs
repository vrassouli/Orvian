using System.Collections.Immutable;
using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.Sample;

public sealed class SamplePlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        Id: "orvian.sample",
        Name: "Sample Plugin",
        Version: new Version(0, 1, 0),
        Publisher: "Orvian",
        Description: "Validates the initial plugin registration contracts.",
        OrvianApiVersion: new Version(0, 1, 0),
        Permissions:
        [
            PluginPermissions.UiNavigationContribute,
            PluginPermissions.CommandReadExecute
        ]);

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Capabilities.Require("shell.posix");
        builder.Navigation.AddPage(
            route: "/sample",
            title: "Sample",
            icon: "puzzle",
            viewModelType: typeof(SamplePageViewModel));
        builder.Features.Add(new SampleSystemProvider());
    }
}

public sealed class SamplePageViewModel
{
    public string Message => "The Orvian plugin architecture is active.";
}

public sealed class SampleSystemProvider : IReadOnlyFeatureProvider
{
    public string FeatureId => "sample-system";

    public string ProviderId => "sample-system.uname";

    public int Priority => 0;

    public IReadOnlySet<string> RequiredCapabilities { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "shell.posix");

    public PluginReadRequest CreateRequest() =>
        new("uname", [new("-s")], TimeSpan.FromSeconds(5));

    public PluginFeatureParseResult Parse(PluginCommandOutput output)
    {
        if (output.IsTruncated || output.ExitCode != 0)
        {
            return PluginFeatureParseResult.Failure(
                "The sample system probe did not return complete output.");
        }

        var value = output.StandardOutput.Trim();
        if (value.Length is 0 or > 128 ||
            value.Contains('\0') ||
            value.Contains('\r') ||
            value.Contains('\n'))
        {
            return PluginFeatureParseResult.Failure(
                "The sample system probe returned malformed output.");
        }

        return new(new(
            ImmutableDictionary.CreateRange(
                StringComparer.Ordinal,
                [KeyValuePair.Create("system", value)])));
    }
}
