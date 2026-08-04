using Orvian.Plugin.Abstractions;

namespace Orvian.Plugins.FileTransfer;

public sealed class FileTransferPlugin : IOrvianPlugin
{
    public PluginManifest Manifest { get; } = new(
        "orvian.file-transfer",
        "File Transfer",
        new Version(0, 1, 0),
        "Orvian",
        "Browse and transfer files between this computer and a selected SSH host.",
        new Version(0, 1, 0),
        [
            PluginPermissions.HostRead,
            PluginPermissions.FileRead,
            PluginPermissions.FileWrite,
            PluginPermissions.UiNavigationContribute
        ]);

    public void Configure(IPluginBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Navigation.AddFeaturePage(
            "/file-transfer",
            "Files",
            "folder",
            typeof(FileTransferPageViewModel),
            "file-transfer");
    }
}

public sealed class FileTransferPageViewModel;
