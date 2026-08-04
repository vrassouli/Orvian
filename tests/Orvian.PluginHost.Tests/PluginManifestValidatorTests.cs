using Orvian.Plugin.Abstractions;
using Orvian.PluginHost;
using Xunit;

namespace Orvian.PluginHost.Tests;

public sealed class PluginManifestValidatorTests
{
    private readonly PluginManifestValidator _validator = new(new Version(0, 1));

    [Fact]
    public void Traversal_entry_assembly_is_rejected()
    {
        var manifest = ValidManifest() with
        {
            EntryAssembly = "../outside.dll"
        };

        var diagnostics = _validator.Validate(manifest, Path.GetTempPath());

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "manifest.assembly_path_invalid");
    }

    [Fact]
    public void Unknown_permission_is_rejected()
    {
        var manifest = ValidManifest() with
        {
            Permissions = ["everything.admin"]
        };

        var diagnostics = _validator.Validate(manifest, Path.GetTempPath());

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "manifest.permission_unknown");
    }

    [Fact]
    public void Newer_api_version_is_incompatible()
    {
        var manifest = ValidManifest() with
        {
            OrvianApiVersion = "0.2.0"
        };

        var diagnostics = _validator.Validate(manifest, Path.GetTempPath());

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Code == "manifest.api_incompatible");
    }

    private static PluginManifestDocument ValidManifest() =>
        new()
        {
            Id = "orvian.test",
            Name = "Test",
            Description = "Test plugin.",
            Publisher = "Orvian",
            Version = "0.1.0",
            OrvianApiVersion = "0.1.0",
            EntryAssembly = "Test.dll",
            EntryType = "Test.Plugin",
            Permissions = [PluginPermissions.UiNavigationContribute]
        };
}
