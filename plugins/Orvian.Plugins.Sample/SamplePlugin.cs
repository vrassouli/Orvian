using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.Sample;

public sealed class SamplePlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        Id: "orvian.sample",
        Name: "Sample Plugin",
        Version: new Version(0, 1, 0),
        Publisher: "Orvian",
        Description: "Validates the initial plugin registration contracts.");

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Capabilities.Require("shell.posix");
        builder.Navigation.AddPage(
            route: "/sample",
            title: "Sample",
            icon: "puzzle",
            viewModelType: typeof(SamplePageViewModel));
    }
}

public sealed class SamplePageViewModel
{
    public string Message => "The Orvian plugin architecture is active.";
}
